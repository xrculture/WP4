using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.Opengl;
using Android.OS;
using Android.Provider;
using Android.Views;
using ARGuidanceMAUI.Models;
using ARGuidanceMAUI.Services;
using Google.AR.Core;
using Google.AR.Core.Exceptions;
using Java.Nio;
using Javax.Microedition.Khronos.Opengles; // OpenGL ES + EGL types (for IGL10, EGLConfig)
using Newtonsoft.Json;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using ArFrame = Google.AR.Core.Frame;

namespace ARGuidanceMAUI.Platforms.Android;

public class ArCoreService : Java.Lang.Object, IArPlatformService, GLSurfaceView.IRenderer
{
    public event Action<GuidanceState>? GuidanceUpdated;
    public event Action<CapturePackage>? CaptureReady;
    public event Action<string>? InfoMessage;
    public event Action<ArDebugTelemetry>? DebugUpdated;

    private readonly Context _ctx;
    private Session? _session;
    private GLSurfaceView? _glView;
    private int _cameraTextureId;

    // Surface/display
    private int _surfaceWidth;
    private int _surfaceHeight;

    // Background rendering
    private int _bgProgram;
    private int _aPosition;
    private int _aTexCoord;
    private int _uTexture;
    private FloatBuffer? _quadVertices;
    private FloatBuffer? _quadTexCoords;
    private FloatBuffer? _quadTexCoordsXf;

    // State
    private float[] _accumCentroid = new float[3];
    private int _centroidCount = 0;
    private Pose? _currentPose = null;
    private float _currentYaw = 0f;
    private int _featuresCount = 0;
    private List<Pose> _poses = new();
    private List<float> _yaws = new();
    private bool _capturing = false;
    private int _captures = 0;
    private const float MinPointConfidence = 0.1f;

    private CameraDevice? _cameraDevice;
    private ImageReader? _imageReader;
    private Handler? _backgroundHandler;
    private HandlerThread? _backgroundThread;

    // Project management
    public string CurrentProjectName { get; private set; } = string.Empty;
    public string CurrentProjectFolder { get; private set; } = string.Empty;

    // Camera capabilities
    private int _cameraWidth = 4032; // Default fallback
    private int _cameraHeight = 3024; // Default fallback

    // Server configuration
    private string _3DReconstructionServerUrl;

    public ArCoreService()
    {
        _ctx = global::Android.App.Application.Context!;
        _3DReconstructionServerUrl = "http://xrculture.rdf.bg:30026/";
        //_3DReconstructionServerUrl = "http://192.168.1.20:5260/"; // localhost
        DetectCameraCapabilities();
    }

    public void AttachGlView(GLSurfaceView? glView)
    {
        _glView = glView;
        if (_glView != null)
        {
            _glView.SetEGLContextClientVersion(2);
            _glView.PreserveEGLContextOnPause = true;
            _glView.SetRenderer(this);
            _glView.RenderMode = Rendermode.Continuously;
        }
    }

    public void NewProject()
    {
        ResetDataStructures();
        CreateNewProject();        
    }

    public void Projects()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var projects = await GetAllProjectsAsync();

                if (projects.Count == 0)
                {
                    InfoMessage?.Invoke("No projects found.");
                    return;
                }

                // Show project list to user
                var projectNames = projects.Select(p => $"{p.Name} - {p.Timestamp:yyyy-MM-dd HH:mm}").ToArray();

                if (Application.Current?.MainPage != null)
                {
                    var selectedProject = await Application.Current.MainPage.DisplayActionSheet(
                        "Select Project",
                        "Cancel",
                        null,
                        projectNames
                    );

                    if (selectedProject != null && selectedProject != "Cancel")
                    {
                        // Find the selected project
                        var index = Array.IndexOf(projectNames, selectedProject);
                        if (index >= 0 && index < projects.Count)
                        {
                            await ShowProjectStatusAsync(projects[index]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                InfoMessage?.Invoke($"Error loading projects: {ex.Message}");
            }
        });
    }

    private async Task<List<ProjectInfo>> GetAllProjectsAsync()
    {
        var projects = new List<ProjectInfo>();

        try
        {
            var context = global::Android.App.Application.Context;
            if (context?.ContentResolver == null)
            {
                return projects;
            }

            // Query for all project.xml files in the ARGuidanceMAUI directory
            var projection = new[]
            {
                global::Android.Provider.BaseColumns.Id,
                MediaStore.MediaColumns.DisplayName,
                MediaStore.MediaColumns.RelativePath,
                MediaStore.MediaColumns.Data
            };

            var selection = $"{MediaStore.MediaColumns.DisplayName} = ? AND {MediaStore.MediaColumns.RelativePath} LIKE ?";
            var selectionArgs = new[] { "project.xml", "Documents/ARGuidanceMAUI/%" };
            var sortOrder = $"{MediaStore.MediaColumns.DateAdded} DESC";

            using var cursor = context.ContentResolver.Query(
                MediaStore.Files.GetContentUri("external"),
                projection,
                selection,
                selectionArgs,
                sortOrder
            );

            if (cursor != null && cursor.MoveToFirst())
            {
                var idColumn = cursor.GetColumnIndex(global::Android.Provider.BaseColumns.Id);
                var pathColumn = cursor.GetColumnIndex(MediaStore.MediaColumns.RelativePath);

                do
                {
                    var id = cursor.GetLong(idColumn);
                    var relativePath = cursor.GetString(pathColumn);

                    // Read the XML file
                    var uri = global::Android.Net.Uri.WithAppendedPath(
                        MediaStore.Files.GetContentUri("external"),
                        id.ToString()
                    );

                    var projectInfo = await ReadProjectXmlAsync(context, uri, relativePath);
                    if (projectInfo != null)
                    {
                        projects.Add(projectInfo);
                    }
                }
                while (cursor.MoveToNext());
            }
        }
        catch (Exception ex)
        {
            InfoMessage?.Invoke($"Error retrieving projects: {ex.Message}");
        }

        return projects;
    }

    private async Task<ProjectInfo?> ReadProjectXmlAsync(Context context, global::Android.Net.Uri uri, string relativePath)
    {
        try
        {
            using var stream = context.ContentResolver.OpenInputStream(uri);
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            var xmlContent = await reader.ReadToEndAsync();

            // Parse XML
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xmlContent);

            var nameNode = doc.SelectSingleNode("//name");
            var descriptionNode = doc.SelectSingleNode("//description");
            var timestampNode = doc.SelectSingleNode("//timestamp");
            var modelNode = doc.SelectSingleNode("//model");
            var taskIdNode = modelNode?.SelectSingleNode("taskId");
            var statusNode = modelNode?.SelectSingleNode("status");
            var errorNode = modelNode?.SelectSingleNode("error");
            var viewUrlNode = modelNode?.SelectSingleNode("viewUrl");
            var downloadUrlNode = modelNode?.SelectSingleNode("downloadUrl");

            if (nameNode == null) return null;

            return new ProjectInfo
            {
                Name = nameNode.InnerText,
                Description = descriptionNode?.InnerText ?? string.Empty,
                Timestamp = DateTime.TryParse(timestampNode?.InnerText, out var timestamp)
                    ? timestamp
                    : DateTime.MinValue,
                FolderPath = relativePath,
                TaskId = taskIdNode?.InnerText ?? string.Empty,
                Status = statusNode?.InnerText ?? string.Empty,
                Error = errorNode?.InnerText ?? string.Empty,
                ViewUrl = viewUrlNode?.InnerText ?? string.Empty,
                DownloadUrl = downloadUrlNode?.InnerText ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            InfoMessage?.Invoke($"Error reading project XML: {ex.Message}");
            return null;
        }
    }

    private async Task ShowProjectStatusAsync(ProjectInfo project)
    {
        if (Application.Current?.MainPage == null) return;

        // Determine available actions based on project state
        var actions = new List<string>() { "Details", "Add Captures" };

        if (string.IsNullOrEmpty(project.TaskId))
        {
            actions.Add("Create 3D Model");
        }
        else
        {
            if ((project.Status == "Pending") || (project.Status == "Running"))
            {
                actions.Add("Check Status");
            }

            if (!string.IsNullOrEmpty(project.ViewUrl))
            {
                actions.Add("View 3D Model");
            }

            if (!string.IsNullOrEmpty(project.DownloadUrl))
            {
                actions.Add("Download Model");
            }
        }

        var action = await Application.Current.MainPage.DisplayActionSheet(
            $"Name: {project.Name}",
            "Cancel",
            null,
            actions.ToArray()
        );

        if (action == "Details")
        {
            await ShowProjectDetailsAsync(project);
        }
        if (action == "Add Captures")
        {
            await AddCaptures(project);
        }
        else if (action == "Create 3D Model")
        {
            await CreateModelForProjectAsync(project);
        }
        else if (action == "View 3D Model" && !string.IsNullOrEmpty(project.ViewUrl))
        {
            await Launcher.OpenAsync(new Uri(project.ViewUrl));
        }
        else if (action == "Download Model" && !string.IsNullOrEmpty(project.DownloadUrl))
        {
            await Launcher.OpenAsync(new Uri(project.DownloadUrl));
        }
        else if (action == "Check Status")
        {
            await CheckModelStatusAsync(project);
        }
    }

    private async Task AddCaptures(ProjectInfo project)
    {
        ResetDataStructures();

        CurrentProjectName = project.Name;
        CurrentProjectFolder = project.FolderPath;

        var imageUris = await GetProjectImagesAsync(project.FolderPath);
        _captures = imageUris.Count;

        var updatedProject = new ProjectInfo
        {
            Name = project.Name,
            Description = project.Description,
            Timestamp = project.Timestamp,
            FolderPath = project.FolderPath,
            TaskId = string.Empty,
            Status = "Pending",
            Error = string.Empty,
            ViewUrl = string.Empty,
            DownloadUrl = string.Empty
        };
        await UpdateProject(updatedProject);
    }

    private async Task ShowProjectDetailsAsync(ProjectInfo project)
    {
        if (Application.Current?.MainPage == null) return;

        var details = $"Name: {project.Name}\n\n" +
                      $"Description: {project.Description}\n\n" +
                      $"Created: {project.Timestamp:yyyy-MM-dd HH:mm:ss}\n\n" +
                      $"Location: {project.FolderPath}\n\n" +
                      $"Task ID: {project.TaskId}\n\n" +
                      $"Status: {project.Status}\n\n" +
                      $"Error: {project.Error}\n\n" +
                      $"View URL: {project.ViewUrl}\n\n" +
                      $"Download URL: {project.DownloadUrl}";
        await ShowMessageAsync("Project Details", details);
    }

    private async Task CreateModelForProjectAsync(ProjectInfo project)
    {
        global::Android.App.ProgressDialog? progressDialog = null;

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var activity = Platform.CurrentActivity;
                if (activity != null && !activity.IsFinishing)
                {
                    progressDialog = global::Android.App.ProgressDialog.Show(
                        activity,
                        "3D Model Creation",
                        "Preparing images and uploading to server...",
                        true,
                        false
                    );
                }
            });

            InfoMessage?.Invoke("Starting 3D model creation...");

            var context = global::Android.App.Application.Context;
            if (context?.ContentResolver == null)
            {
                InfoMessage?.Invoke("Content resolver not available.");
                return;
            }

            // Query MediaStore for images in the project folder
            var imageUris = await GetProjectImagesAsync(project.FolderPath);

            if (imageUris.Count == 0)
            {
                await ShowMessageAsync("3D Model Creation", "No images found in the project folder.");
                return;
            }

            InfoMessage?.Invoke($"Found {imageUris.Count} images. Ready to create 3D model.");

            // Create zip file in temp folder
            var tempFolder = System.IO.Path.GetTempPath();
            var zipFileName = $"project_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var zipFilePath = System.IO.Path.Combine(tempFolder, zipFileName);

            try
            {
                using (var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
                {
                    int imageIndex = 0;
                    foreach (var uri in imageUris)
                    {
                        using var stream = context.ContentResolver.OpenInputStream(uri);
                        if (stream != null)
                        {
                            var fileName = $"image_{imageIndex:D4}.jpg";
                            var zipEntry = zipArchive.CreateEntry(fileName, CompressionLevel.Optimal);
                            using var entryStream = zipEntry.Open();
                            await stream.CopyToAsync(entryStream);
                            imageIndex++;
                        }
                    }
                }

                InfoMessage?.Invoke($"Created zip file: {zipFilePath} with {imageUris.Count} images.");

                var taskId = await Create3DModelTask(project.Name, zipFilePath);
                if (!string.IsNullOrEmpty(taskId))
                {
                    InfoMessage?.Invoke($"3D model creation task started. Task ID: {taskId}");

                    var updatedProject = project with { TaskId = taskId, Status = "Pending" };
                    await UpdateProject(updatedProject);

                    ResetDataStructures();
                }
                else
                {
                    InfoMessage?.Invoke("Failed to start 3D model creation task.");
                }
            }
            catch (Exception ex)
            {
                InfoMessage?.Invoke($"Error creating zip file: {ex.Message}");
            }
            finally
            {
                if (File.Exists(zipFilePath))
                {
                    try
                    {
                        File.Delete(zipFilePath);
                        InfoMessage?.Invoke("Temporary zip file deleted.");
                    }
                    catch (Exception ex)
                    {
                        InfoMessage?.Invoke($"Error deleting temp zip file: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            InfoMessage?.Invoke($"Error creating 3D model: {ex.Message}");
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    progressDialog?.Dismiss();
                    progressDialog?.Dispose();
                }
                catch
                {
                }
            });
        }
    }

    private async Task<string?> Create3DModelTask(string model, string zipFilePath)
    {
        var url = $"{_3DReconstructionServerUrl}TaskManager?handler=Create3DModel";

        string create3DModelRequest =
            @"<Create3DModelRequest>
                    <Model>%MODEL%</Model>
                    <Workflow>%WORKFLOW%</Workflow>
                </Create3DModelRequest>";

        try
        {
            create3DModelRequest = create3DModelRequest.
                Replace("%MODEL%", model).
                Replace("%WORKFLOW%", "openMVG-openMVS");

            using (var handler = new HttpClientHandler())
            {
                handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; };

                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMinutes(60);

                    using (var form = new MultipartFormDataContent())
                    {
                        form.Add(new StringContent(create3DModelRequest, System.Text.Encoding.UTF8, "application/xml"), "request", "request.xml");

                        using (var fileStream = File.OpenRead(zipFilePath))
                        {
                            if (fileStream == null || fileStream.Length == 0)
                            {
                                await ShowMessageAsync("Error", "File stream is null or empty. Please check the file path.");
                                return null;
                            }

                            var fileContent = new StreamContent(fileStream);
                            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                            form.Add(fileContent, "file", "model.zip");

                            var response = await RetryPostAsync(client, url, form);
                            if (response == null)
                            {
                                await ShowMessageAsync("Error", "No response from server.");
                                return null;
                            }

                            string responseString = await response.Content.ReadAsStringAsync();
                            Console.WriteLine(responseString);

                            var xmlDoc = new XmlDocument();
                            xmlDoc.LoadXml(responseString);

                            var status = xmlDoc.SelectSingleNode("//Status")?.InnerText;
                            if (status?.Trim() != "200")
                            {
                                var message = xmlDoc.SelectSingleNode("//Message")?.InnerText;
                                await ShowMessageAsync("Error", $"Server returned error status: '{status}'. Message: '{message}'.");
                                return null;
                            }

                            var taskId = xmlDoc.SelectSingleNode("//Parameters/TaskId")?.InnerText;
                            if (string.IsNullOrEmpty(taskId))
                            {
                                await ShowMessageAsync("Error", "Server did not return a 'TaskId'.");
                                return null;
                            }

                            await ShowMessageAsync("3D Model Creation", $"Task created successfully. Task ID: {taskId}");

                            return taskId;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Error", $"Failed to create 3D model: '{ex.Message}'.");
        }

        return null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                if (Application.Current?.MainPage != null)
                {
                    await Application.Current.MainPage.DisplayAlert(title, message, "OK");
                }
                else
                {
                    InfoMessage?.Invoke($"{title}: {message}");
                }
            }
            catch
            {
                InfoMessage?.Invoke($"{title}: {message}");
                System.Diagnostics.Debug.WriteLine($"{title}: {message}");
            }
        });
    }

    public async Task<HttpResponseMessage?> RetryPostAsync(HttpClient client, string url, HttpContent content, int maxRetries = 3)
    {
        int retryCount = 0;
        HttpResponseMessage? response = null;

        while (retryCount < maxRetries)
        {
            try
            {
                response = await client.PostAsync(url, content);
                return response;
            }
            catch (HttpRequestException ex)
            {
                retryCount++;
                if (retryCount >= maxRetries)
                    throw;

                int delayMs = (int)Math.Pow(2, retryCount) * 1000;
                await Task.Delay(delayMs);
            }
        }

        return response;
    }

    private async Task<List<global::Android.Net.Uri>> GetProjectImagesAsync(string projectFolderPath)
    {
        var imageUris = new List<global::Android.Net.Uri>();

        try
        {
            var context = global::Android.App.Application.Context;
            if (context?.ContentResolver == null) return imageUris;

            // Convert Documents path to Pictures path
            var imageFolderPath = projectFolderPath.Replace("Documents/", "Pictures/");

            // Query for images in the Pictures directory matching the project folder
            var projection = new[]
            {
                global::Android.Provider.BaseColumns.Id,
                MediaStore.MediaColumns.DisplayName,
                MediaStore.MediaColumns.RelativePath
            };

            var selection = $"{MediaStore.MediaColumns.RelativePath} LIKE ?";
            var selectionArgs = new[] { $"{imageFolderPath}%" };

            using var cursor = context.ContentResolver.Query(
                MediaStore.Images.Media.ExternalContentUri,
                projection,
                selection,
                selectionArgs,
                null
            );

            if (cursor != null && cursor.MoveToFirst())
            {
                var idColumn = cursor.GetColumnIndex(global::Android.Provider.BaseColumns.Id);

                do
                {
                    var id = cursor.GetLong(idColumn);
                    var uri = global::Android.Net.Uri.WithAppendedPath(
                        MediaStore.Images.Media.ExternalContentUri,
                        id.ToString()
                    );
                    imageUris.Add(uri);
                }
                while (cursor.MoveToNext());
            }
        }
        catch (Exception ex)
        {
            InfoMessage?.Invoke($"Error querying images: {ex.Message}");
        }

        return imageUris;
    }

    private async Task CheckModelStatusAsync(ProjectInfo project)
    {
        try
        {
            InfoMessage?.Invoke("Checking model status...");

            using (var handler = new HttpClientHandler())
            {
                handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; };

                using (var client = new HttpClient(handler))
                {
                    var taskStatusUrl = $"{_3DReconstructionServerUrl}TaskManager?handler=TaskStatus&taskId={project.TaskId}";
                    var response = await client.GetAsync(taskStatusUrl.ToString());
                    var responseString = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(responseString);

                    // Try JSON first
                    try
                    {
                        dynamic jsonResponse = JsonConvert.DeserializeObject(responseString);
                        if (jsonResponse != null)
                        {
                            var status = jsonResponse?.status ?? string.Empty;
                            var error = jsonResponse?.error ?? string.Empty;
                            var viewUrl = jsonResponse?.viewUrl ?? string.Empty;
                            if (!string.IsNullOrEmpty(viewUrl?.ToString()))
                            {
                                viewUrl = _3DReconstructionServerUrl.TrimEnd('/') + viewUrl?.ToString();
                            }
                            var downloadUrl = jsonResponse?.downloadUrl ?? string.Empty;
                            if (!string.IsNullOrEmpty(downloadUrl?.ToString()))
                            {
                                downloadUrl = _3DReconstructionServerUrl.TrimEnd('/') + downloadUrl?.ToString();
                            }

                            var updatedProject = project with
                            {
                                Status = status,
                                Error = error,
                                ViewUrl = viewUrl,
                                DownloadUrl = downloadUrl
                            };

                            await UpdateProject(updatedProject);
                            await ShowProjectDetailsAsync(updatedProject);
                            return;
                        }
                    }
                    catch (JsonException)
                    {
                        // Not JSON
                    }

                    // Try XML
                    try
                    {
                        var xmlDoc = new XmlDocument();
                        xmlDoc.LoadXml(responseString);

                        var message = xmlDoc.SelectSingleNode("//Message")?.InnerText;
                        await ShowMessageAsync("Error", $"Server returned error: '{message}'.");
                    }
                    catch (XmlException)
                    {
                        await ShowMessageAsync("Error", "Unable to parse response as JSON or XML.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Error", $"Error checking status: {ex.Message}");
        }
    }

    private void CreateNewProject()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var result = await ShowProjectDialogAsync();

                if (result != null)
                {
                    var now = DateTime.Now;
                    var sanitizedName = SanitizeFileName(result.Name);
                    var folderName = string.IsNullOrWhiteSpace(sanitizedName)
                        ? $"{now:yyyy-MM-dd-HH-mm-ss}"
                        : $"{now:yyyy-MM-dd-HH-mm-ss}_{sanitizedName}";

                    // Use Documents for XML files, Pictures for images
                    CurrentProjectName = result.Name;
                    CurrentProjectFolder = $"Documents/ARGuidanceMAUI/{folderName}";

                    // Create project.xml
                    StringBuilder sbProjectXML = new();
                    sbProjectXML.Append("<project>");
                    sbProjectXML.Append("<name>");
                    sbProjectXML.Append(result.Name);
                    sbProjectXML.Append("</name>");
                    sbProjectXML.Append("<description>");
                    sbProjectXML.Append(result.Description);
                    sbProjectXML.Append("</description>");
                    sbProjectXML.Append("<timestamp>");
                    sbProjectXML.Append(DateTime.UtcNow.ToString("o"));
                    sbProjectXML.Append("</timestamp>");
                    sbProjectXML.Append("<model>");
                    sbProjectXML.Append("<taskId>");
                    sbProjectXML.Append("</taskId>");
                    sbProjectXML.Append("<status>");
                    sbProjectXML.Append("</status>");
                    sbProjectXML.Append("<error>");
                    sbProjectXML.Append("</error>");
                    sbProjectXML.Append("<viewUrl>");
                    sbProjectXML.Append("<![CDATA[]]>");
                    sbProjectXML.Append("</viewUrl>");
                    sbProjectXML.Append("<downloadUrl>");
                    sbProjectXML.Append("<![CDATA[]]>");
                    sbProjectXML.Append("</downloadUrl>");
                    sbProjectXML.Append("</model>");
                    sbProjectXML.Append("</project>");

                    // Save XML file
                    await SaveProjectXmlAsync(CurrentProjectFolder, sbProjectXML.ToString());

                    InfoMessage?.Invoke($"Project created: {result.Name}");
                }
                else
                {
                    InfoMessage?.Invoke("Project creation cancelled.");
                }
            }
            catch (Exception ex)
            {
                InfoMessage?.Invoke($"Error creating project: {ex.Message}");
            }
        });
    }

    private async Task UpdateProject(ProjectInfo projectInfo)
    {
        StringBuilder sbProjectXML = new();
        sbProjectXML.Append("<project>");
        sbProjectXML.Append("<name>");
        sbProjectXML.Append(projectInfo.Name);
        sbProjectXML.Append("</name>");
        sbProjectXML.Append("<description>");
        sbProjectXML.Append(projectInfo.Description);
        sbProjectXML.Append("</description>");
        sbProjectXML.Append("<timestamp>");
        sbProjectXML.Append(DateTime.UtcNow.ToString("o"));
        sbProjectXML.Append("</timestamp>");
        sbProjectXML.Append("<model>");
        sbProjectXML.Append("<taskId>");
        sbProjectXML.Append(projectInfo.TaskId);
        sbProjectXML.Append("</taskId>");
        sbProjectXML.Append("<status>");
        sbProjectXML.Append(projectInfo.Status);
        sbProjectXML.Append("</status>");
        sbProjectXML.Append("<error>");
        sbProjectXML.Append(projectInfo.Error);
        sbProjectXML.Append("</error>");
        sbProjectXML.Append("<viewUrl><![CDATA[");
        sbProjectXML.Append(projectInfo.ViewUrl);
        sbProjectXML.Append("]]></viewUrl>");
        sbProjectXML.Append("<downloadUrl><![CDATA[");
        sbProjectXML.Append(projectInfo.DownloadUrl);
        sbProjectXML.Append("]]></downloadUrl>");
        sbProjectXML.Append("</model>");
        sbProjectXML.Append("</project>");

        // Save XML file
        await SaveProjectXmlAsync(projectInfo.FolderPath, sbProjectXML.ToString());
    }

    private async Task SaveProjectXmlAsync(string relativePath, string xmlContent)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            if (context?.ContentResolver == null)
            {
                InfoMessage?.Invoke("Content resolver not available.");
                return;
            }

            // Try to find existing file first
            var existingUri = await FindProjectXmlUriAsync(relativePath);

            global::Android.Net.Uri? uri;

            if (existingUri != null)
            {
                // File exists - update it
                uri = existingUri;
                InfoMessage?.Invoke($"Updating existing project XML at: {relativePath}");
            }
            else
            {
                // File doesn't exist - create new
                var values = new ContentValues();
                values.Put(MediaStore.IMediaColumns.DisplayName, "project.xml");
                values.Put(MediaStore.IMediaColumns.MimeType, "text/xml");
                values.Put(MediaStore.IMediaColumns.RelativePath, relativePath);

                uri = context.ContentResolver.Insert(MediaStore.Files.GetContentUri("external"), values);
                if (uri == null)
                {
                    InfoMessage?.Invoke("Failed to create XML file via MediaStore.");
                    return;
                }
                InfoMessage?.Invoke($"Creating new project XML at: {relativePath}");
            }

            // Write the XML content (mode "wt" = write + truncate)
            await Task.Run(() =>
            {
                using var stream = context.ContentResolver.OpenOutputStream(uri, "wt");
                if (stream != null)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(xmlContent);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
            });

            InfoMessage?.Invoke($"Project XML saved successfully.");
        }
        catch (Exception ex)
        {
            InfoMessage?.Invoke($"Error saving project XML: {ex.Message}");
            throw;
        }
    }

    private async Task<global::Android.Net.Uri?> FindProjectXmlUriAsync(string relativePath)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            if (context?.ContentResolver == null) return null;

            var projection = new[] { global::Android.Provider.BaseColumns.Id };
            var selection = $"{MediaStore.MediaColumns.DisplayName} = ? AND {MediaStore.MediaColumns.RelativePath} = ?";
            var selectionArgs = new[] { "project.xml", relativePath };

            using var cursor = context.ContentResolver.Query(
                MediaStore.Files.GetContentUri("external"),
                projection,
                selection,
                selectionArgs,
                null
            );

            if (cursor != null && cursor.MoveToFirst())
            {
                var idColumn = cursor.GetColumnIndex(global::Android.Provider.BaseColumns.Id);
                var id = cursor.GetLong(idColumn);
                return global::Android.Net.Uri.WithAppendedPath(
                    MediaStore.Files.GetContentUri("external"),
                    id.ToString()
                );
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ProjectInfo?> ShowProjectDialogAsync()
    {
        string? projectName = null;
        string? projectDescription = null;

        if (Application.Current?.MainPage != null)
        {
            projectName = await Application.Current.MainPage.DisplayPromptAsync(
                "New Project",
                "Enter project name:",
                accept: "Next",
                cancel: "Cancel",
                placeholder: "Project name",
                maxLength: 50
            );

            if (projectName != null)
            {
                projectDescription = await Application.Current.MainPage.DisplayPromptAsync(
                    "New Project",
                    "Enter project description:",
                    accept: "OK",
                    cancel: "Cancel",
                    placeholder: "Description (optional)",
                    maxLength: 200
                );

                if (projectDescription != null)
                {
                    return new ProjectInfo
                    {
                        Name = projectName,
                        Description = projectDescription
                    };
                }
            }
        }

        return null;
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Trim();
    }

    private void ResetDataStructures()
    {
        // Reset project info
        CurrentProjectName = string.Empty;
        CurrentProjectFolder = string.Empty;

        // Reset all AR tracking data
        _accumCentroid = new float[3];
        _centroidCount = 0;
        _currentPose = null;
        _currentYaw = 0f;
        _featuresCount = 0;
        _poses = new();
        _yaws = new();
        _capturing = false;
        _captures = 0;
    }

    private void StartBackgroundThread()
    {
        _backgroundThread = new HandlerThread("CameraBackground");
        _backgroundThread.Start();
        _backgroundHandler = new Handler(_backgroundThread.Looper!);
    }

    private void StopBackgroundThread()
    {
        _backgroundThread?.QuitSafely();
        _backgroundThread = null;
        _backgroundHandler = null;
    }

    public void Start()
    {
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            if (!await EnsureCameraPermissionAsync())
            {
                GuidanceUpdated?.Invoke(new GuidanceState { Hint = "Camera permission is required to start AR." });
                return;
            }

            if (!await EnsureStorageWritePermissionAsync())
            {
                GuidanceUpdated?.Invoke(new GuidanceState { Hint = "Storage permission is required to start AR." });
                return;
            }

            if (_session == null)
            {
                try
                {
                    InfoMessage?.Invoke("Checking ARCore availability...");

                    var availability = ArCoreApk.Instance.CheckAvailability(_ctx);
                    InfoMessage?.Invoke($"ARCore availability status: {availability}");

                    if (availability.IsUnknown)
                    {
                        await Task.Delay(200);
                        availability = ArCoreApk.Instance.CheckAvailability(_ctx);
                        InfoMessage?.Invoke($"ARCore availability after wait: {availability}");
                    }

                    if (!availability.IsSupported)
                    {
                        GuidanceUpdated?.Invoke(new GuidanceState
                        {
                            Hint = $"ARCore not supported. Status: {availability}"
                        });
                        return;
                    }

                    InfoMessage?.Invoke("Creating ARCore session...");
                    _session = new Session(_ctx, [Session.Feature.SharedCamera]);
                    InfoMessage?.Invoke("ARCore session created successfully");
                }
                catch (Java.Lang.ClassNotFoundException ex)
                {
                    GuidanceUpdated?.Invoke(new GuidanceState
                    {
                        Hint = $"ARCore class not found (ProGuard issue): {ex.Message}"
                    });
                    return;
                }
                catch (UnavailableException e)
                {
                    GuidanceUpdated?.Invoke(new GuidanceState
                    {
                        Hint = $"ARCore unavailable: {e.Message}"
                    });
                    return;
                }
                catch (Exception e)
                {
                    GuidanceUpdated?.Invoke(new GuidanceState
                    {
                        Hint = $"ARCore error: {e.GetType().Name} - {e.Message}"
                    });
                    return;
                }

                var config = new Config(_session);
                config.SetUpdateMode(Config.UpdateMode.LatestCameraImage);
                config.SetFocusMode(Config.FocusMode.Auto);
                config.SetPlaneFindingMode(Config.PlaneFindingMode.HorizontalAndVertical);
                config.SetDepthMode(Config.DepthMode.Automatic);
                config.SetLightEstimationMode(Config.LightEstimationMode.EnvironmentalHdr);
                _session?.Configure(config);

                StartBackgroundThread();
            }

            try
            {
                _session?.Resume();
                _glView?.OnResume();

                if (_session != null && _cameraTextureId != 0)
                    _session.SetCameraTextureName(_cameraTextureId);
            }
            catch { }
        }
        catch (Exception e)
        {
            GuidanceUpdated?.Invoke(new GuidanceState { Hint = $"Error starting AR: {e.Message}" });
        }
    }

    private static async Task<bool> EnsureCameraPermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
        }
        return status == PermissionStatus.Granted;
    }

    private static async Task<bool> EnsureStorageWritePermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.StorageWrite>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.StorageWrite>();
        }
        return status == PermissionStatus.Granted;
    }

    public void Stop()
    {
        try { _glView?.OnPause(); _session?.Pause(); } catch { }
    }

    public void RequestCapture()
    {
        if (string.IsNullOrEmpty(CurrentProjectFolder))
        {
            InfoMessage?.Invoke("No active project. Create a new project first.");
            return;
        }

        if (_currentPose == null)
        {
            InfoMessage?.Invoke("AR tracking not ready. Move the device to initialize.");
            return;
        }

        // #todo: Check for sufficient feature points
        //if (_featuresCount == 0)
        //{
        //    InfoMessage?.Invoke("Not enough feature points detected. Move around to add more features.");
        //    return;
        //}

        if (_capturing)
        {
            InfoMessage?.Invoke("Already capturing. Please wait.");
            return;
        }

        _capturing = true;

        if (_imageReader == null)
        {
            _imageReader = ImageReader.NewInstance(_cameraWidth, _cameraHeight, ImageFormatType.Jpeg, 2);
            _imageReader.SetOnImageAvailableListener(new ImageAvailableListener(OnImageAvailable), _backgroundHandler);
        }

        var cameraManager = _ctx.GetSystemService(Context.CameraService) as CameraManager;
        string cameraId = cameraManager?.GetCameraIdList().First()!;
        cameraManager?.OpenCamera(cameraId, new CameraStateCallback(this), _backgroundHandler);
    }

    // GLSurfaceView.IRenderer
    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        _cameraTextureId = CreateCameraTextureExternal();
        _session?.SetCameraTextureName(_cameraTextureId);

        InitBackgroundRenderer();
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        _surfaceWidth = width;
        _surfaceHeight = height;
        GLES20.GlViewport(0, 0, width, height);

        if (_session != null && width > 0 && height > 0)
        {
            _session.SetDisplayGeometry((int)GetDisplayRotation(), width, height);
        }
    }

    public void OnDrawFrame(IGL10? gl)
    {
        GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);

        try
        {
            if (_session == null)
            {
                return;
            }

            if (_capturing)
            {
                GuidanceUpdated?.Invoke(new GuidanceState { Hint = "Capturing..." });
                return;
            }

            if (_surfaceWidth > 0 && _surfaceHeight > 0)
            {
                _session.SetDisplayGeometry((int)GetDisplayRotation(), _surfaceWidth, _surfaceHeight);
            }

            var frame = _session.Update();
            var cam = frame.Camera;
            _currentPose = cam.Pose;

            // Background
            UpdateBackgroundUv(frame);
            DrawBackground();

            if (string.IsNullOrEmpty(CurrentProjectFolder))
            {
                GuidanceUpdated?.Invoke(new GuidanceState { Hint = "No active project. Create a new project first." });
                return;
            }

            if (cam.TrackingState != TrackingState.Tracking)
            {
                GuidanceUpdated?.Invoke(new GuidanceState { Hint = "Initializing..." });
                return;
            }

            using var pointCloud = frame.AcquirePointCloud();
            var idsBuf = pointCloud.Ids;
            var ptsBuf = pointCloud.Points;

            // Reset positions
            idsBuf.Position(0);
            ptsBuf.Position(0);

            // Read raw IDs
            var allIds = ReadAllIds(idsBuf);

            // Read filtered IDs
            idsBuf.Position(0);
            var filteredIds = FilterIdsByConfidence(idsBuf, ptsBuf, MinPointConfidence);

            // Extract feature point data for visualization
            var allFeaturePoints = ExtractFeaturePoints(idsBuf, ptsBuf, allIds);
            var filteredFeaturePoints = ExtractFeaturePoints(idsBuf, ptsBuf, filteredIds);

            _featuresCount = allFeaturePoints.Length;
            var hint = _featuresCount > 0
                ? "Ready for a capture..."
                : "Move around to add features...";

            var deltaPose = 0f;
            var deltaYaw = 0f;
            if (_featuresCount > 0)
            {
                // Accumulate centroid for yaw calculation
                AccumulateCentroid(ptsBuf);
                var centroid = _centroidCount > 0 ? _accumCentroid : null;
                _currentYaw = YawRad(_currentPose, centroid);

                if (_poses.Count > 0)
                {
                    var lastPose = _poses.Last();
                    deltaPose = TranslationDelta(_currentPose, lastPose);

                    var lastYaw = _yaws.Last();
                    deltaYaw = DeltaYaw(_currentYaw, lastYaw);
                }
            }

            GuidanceUpdated?.Invoke(new GuidanceState { Hint = hint });
            var telemetry = new ArDebugTelemetry
            {
                ProjectName = CurrentProjectName,
                DeltaPositionMeters = deltaPose,
                DeltaYawRad = deltaYaw,
                AllFeaturePoints = allFeaturePoints,
                FilteredFeaturePoints = filteredFeaturePoints,
                Captures = _captures
            };
            DebugUpdated?.Invoke(telemetry);

            Views.FeaturePointsDrawable.RenderFeaturePointsOpenGL(allFeaturePoints, filteredFeaturePoints, _surfaceWidth, _surfaceHeight);
        }
        catch (Exception e)
        {
            GuidanceUpdated?.Invoke(new GuidanceState { Hint = $"Error: {e.Message}" });
        }
    }

    private SurfaceOrientation GetDisplayRotation()
    {
        try
        {
            var d = _glView?.Display;
            if (d != null) return d.Rotation;

#pragma warning disable CA1422
            var wm = _ctx.GetSystemService(Context.WindowService) as IWindowManager;
            var disp = wm?.DefaultDisplay;
            return disp != null ? disp.Rotation : SurfaceOrientation.Rotation0;
#pragma warning restore CA1422
        }
        catch
        {
            return SurfaceOrientation.Rotation0;
        }
    }

    // Background rendering
    private void InitBackgroundRenderer()
    {
        const string vsh = @"
attribute vec4 a_Position;
attribute vec2 a_TexCoord;
varying vec2 v_TexCoord;
void main() {
  v_TexCoord = a_TexCoord;
  gl_Position = a_Position;
}";
        const string fsh = @"
#extension GL_OES_EGL_image_external : require
precision mediump float;
varying vec2 v_TexCoord;
uniform samplerExternalOES uTexture;
void main() {
  gl_FragColor = texture2D(uTexture, v_TexCoord);
}";

        _bgProgram = CreateProgram(vsh, fsh);
        _aPosition = GLES20.GlGetAttribLocation(_bgProgram, "a_Position");
        _aTexCoord = GLES20.GlGetAttribLocation(_bgProgram, "a_TexCoord");
        _uTexture = GLES20.GlGetUniformLocation(_bgProgram, "uTexture");

        // Full-screen quad (triangle strip)
        var verts = new float[]
        {
            -1f,-1f,
             1f,-1f,
            -1f, 1f,
             1f, 1f
        };
        var uvs = new float[]
        {
            0f,1f,
            1f,1f,
            0f,0f,
            1f,0f
        };

        _quadVertices = ByteBuffer
            .AllocateDirect(verts.Length * sizeof(float))
            .Order(ByteOrder.NativeOrder()!)
            .AsFloatBuffer();
        _quadVertices.Put(verts);
        _quadVertices.Position(0);

        _quadTexCoords = ByteBuffer
            .AllocateDirect(uvs.Length * sizeof(float))
            .Order(ByteOrder.NativeOrder()!)
            .AsFloatBuffer();
        _quadTexCoords.Put(uvs);
        _quadTexCoords.Position(0);

        // buffer for transformed uvs
        _quadTexCoordsXf = ByteBuffer
            .AllocateDirect(uvs.Length * sizeof(float))
            .Order(ByteOrder.NativeOrder()!)
            .AsFloatBuffer();
        _quadTexCoordsXf.Position(0);
    }

    // Use transformed UVs if present
    private void DrawBackground()
    {
        if (_bgProgram == 0 || _cameraTextureId == 0 || _quadVertices == null)
            return;

        var texBuf = _quadTexCoordsXf ?? _quadTexCoords;
        if (texBuf == null) return;

        GLES20.GlUseProgram(_bgProgram);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _cameraTextureId);
        GLES20.GlUniform1i(_uTexture, 0);

        GLES20.GlEnableVertexAttribArray(_aPosition);
        GLES20.GlEnableVertexAttribArray(_aTexCoord);

        GLES20.GlVertexAttribPointer(_aPosition, 2, GLES20.GlFloat, false, 0, _quadVertices);
        GLES20.GlVertexAttribPointer(_aTexCoord, 2, GLES20.GlFloat, false, 0, texBuf);

        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);

        GLES20.GlDisableVertexAttribArray(_aPosition);
        GLES20.GlDisableVertexAttribArray(_aTexCoord);
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, 0);
        GLES20.GlUseProgram(0);
    }

    private static int CompileShader(int type, string src)
    {
        int shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, src);
        GLES20.GlCompileShader(shader);
        int[] compiled = new int[1];
        GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, compiled, 0);
        if (compiled[0] == 0)
        {
            string? log = GLES20.GlGetShaderInfoLog(shader);
            GLES20.GlDeleteShader(shader);
            throw new InvalidOperationException($"Shader compile failed: {log}");
        }
        return shader;
    }

    private static int CreateProgram(string vsh, string fsh)
    {
        int vs = CompileShader(GLES20.GlVertexShader, vsh);
        int fs = CompileShader(GLES20.GlFragmentShader, fsh);
        int prog = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(prog, vs);
        GLES20.GlAttachShader(prog, fs);
        GLES20.GlLinkProgram(prog);
        int[] linked = new int[1];
        GLES20.GlGetProgramiv(prog, GLES20.GlLinkStatus, linked, 0);
        if (linked[0] == 0)
        {
            string? log = GLES20.GlGetProgramInfoLog(prog);
            GLES20.GlDeleteProgram(prog);
            throw new InvalidOperationException($"Program link failed: {log}");
        }
        GLES20.GlDeleteShader(vs);
        GLES20.GlDeleteShader(fs);
        return prog;
    }

    private static int CreateCameraTextureExternal()
    {
        var textures = new int[1];
        GLES20.GlGenTextures(1, textures, 0);
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, textures[0]);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        return textures[0];
    }

    private void AccumulateCentroid(FloatBuffer pts)
    {
        // Each point: X,Y,Z,confidence (4 floats)
        pts.Position(0);
        int total = pts.Limit() / 4;
        if (total == 0) return;

        int count = 0; float sx = 0, sy = 0, sz = 0;
        int max = Math.Min(total, 2000);
        for (int i = 0; i < max; i++)
        {
            sx += pts.Get(); // X
            sy += pts.Get(); // Y
            sz += pts.Get(); // Z
            pts.Get();       // confidence (skip)
            count++;
        }

        var cx = sx / count; var cy = sy / count; var cz = sz / count;
        _accumCentroid[0] = (_accumCentroid[0] * _centroidCount + cx) / (_centroidCount + 1);
        _accumCentroid[1] = (_accumCentroid[1] * _centroidCount + cy) / (_centroidCount + 1);
        _accumCentroid[2] = (_accumCentroid[2] * _centroidCount + cz) / (_centroidCount + 1);
        _centroidCount++;
    }

    private static float TranslationDelta(Pose a, Pose b)
    {
        var at = a.GetTranslation(); var bt = b.GetTranslation();
        var dx = at[0] - bt[0]; var dy = at[1] - bt[1]; var dz = at[2] - bt[2];
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float YawRad(Pose pose, float[]? target)
    {
        var t = pose.GetTranslation();
        if (target != null)
        {
            var vx = t[0] - target[0];
            var vz = t[2] - target[2];
            return (float)Math.Atan2(vx, vz);
        }
        var forward = new float[] { 0, 0, -1 };
        var outV = new float[3];
        pose.RotateVector(forward, 0, outV, 0);
        return (float)Math.Atan2(outV[0], -outV[2]);
    }

    private static long[] FilterIdsByConfidence(Java.Nio.IntBuffer idsBuf, Java.Nio.FloatBuffer ptsBuf, float minConf, int max = 4000)
    {
        // ids[i] corresponds to point at floats [i*4 .. i*4+3] where +3 is confidence
        idsBuf.Position(0);
        int idsCount = idsBuf.Limit();
        int[] idsInt = new int[idsCount];
        idsBuf.Get(idsInt, 0, idsCount);

        int totalPts = ptsBuf.Limit() / 4;
        int n = Math.Min(idsCount, totalPts);

        var list = new List<long>(Math.Min(n, max));
        for (int i = 0; i < n && list.Count < max; i++)
        {
            float conf = ptsBuf.Get(i * 4 + 3); // absolute read; does not change position
            if (conf >= minConf) list.Add(idsInt[i]);
        }
        return list.ToArray();
    }

    private static long[] ReadAllIds(Java.Nio.IntBuffer idsBuf)
    {
        idsBuf.Position(0);
        int count = idsBuf.Limit();
        if (count <= 0) return Array.Empty<long>();
        var tmp = new int[count];
        idsBuf.Get(tmp, 0, count);
        var res = new long[count];
        for (int i = 0; i < count; i++) res[i] = tmp[i];
        return res;
    }

    private static FeaturePoint[] ExtractFeaturePoints(Java.Nio.IntBuffer idsBuf, Java.Nio.FloatBuffer ptsBuf, long[] ids)
    {
        if (ids.Length == 0) return Array.Empty<FeaturePoint>();

        var result = new FeaturePoint[ids.Length];
        var idsInt = new int[idsBuf.Limit()];
        idsBuf.Position(0);
        idsBuf.Get(idsInt, 0, idsInt.Length);

        int resultIndex = 0;
        for (int i = 0; i < idsInt.Length && resultIndex < ids.Length; i++)
        {
            if (ids.Contains(idsInt[i]))
            {
                // Read point data: X, Y, Z, confidence
                var x = ptsBuf.Get(i * 4);
                var y = ptsBuf.Get(i * 4 + 1);
                var z = ptsBuf.Get(i * 4 + 2);
                var confidence = ptsBuf.Get(i * 4 + 3);

                result[resultIndex++] = new FeaturePoint
                {
                    Id = idsInt[i],
                    X = x,
                    Y = y,
                    Z = z,
                    Confidence = confidence
                };
            }
        }

        // Resize array if needed
        if (resultIndex < result.Length)
        {
            Array.Resize(ref result, resultIndex);
        }

        return result;
    }

    private static float DeltaYaw(float current, float target)
    {
        var d = target - current;
        while (d > Math.PI) d -= (float)(2 * Math.PI);
        while (d < -Math.PI) d += (float)(2 * Math.PI);
        return d;
    }

    private void UpdateBackgroundUv(ArFrame frame)
    {
        if (_quadTexCoords == null || _quadTexCoordsXf == null)
            return;

        try
        {
            _quadTexCoords.Position(0);
            _quadTexCoordsXf.Position(0);

#pragma warning disable CS0618 // Type or member is obsolete
            frame.TransformDisplayUvCoords(_quadTexCoords, _quadTexCoordsXf);
#pragma warning restore CS0618 // Type or member is obsolete

            _quadTexCoordsXf.Position(0);
        }
        catch (Java.Lang.NoSuchMethodError)
        {
        }
    }

    private void OnImageAvailable(ImageReader? reader)
    {
        try
        {
            using (var image = reader?.AcquireLatestImage())
            {
                if (image != null)
                {
                    var buffer = image.GetPlanes()?[0].Buffer;
                    if (buffer != null)
                    {
                        byte[] jpegBytes = new byte[buffer.Remaining()];
                        buffer.Get(jpegBytes);

                        CaptureReady?.Invoke(new CapturePackage
                        {
                            JpegBytes = jpegBytes,
                            MetadataJson = "",
                            FileBaseName = $"cap_{image.Timestamp}"
                        });
                    }
                    image.Close();
                }
            }

            _poses.Add(_currentPose!);
            _yaws.Add(_currentYaw);
            _captures++;
        }
        catch (Exception ex)
        {
            InfoMessage?.Invoke($"Capture error: {ex.Message}");
        }
        finally
        {
            _capturing = false;
        }
    }

    private void DetectCameraCapabilities()
    {
        try
        {
            var cameraManager = _ctx.GetSystemService(Context.CameraService) as CameraManager;
            if (cameraManager != null)
            {
                var cameraIds = cameraManager.GetCameraIdList();
                if (cameraIds.Length > 0)
                {
                    var characteristics = cameraManager.GetCameraCharacteristics(cameraIds[0]);
                    var map = characteristics?.Get(CameraCharacteristics.ScalerStreamConfigurationMap) as StreamConfigurationMap;
                    var sizes = map?.GetOutputSizes((int)ImageFormatType.Jpeg);

                    if (sizes != null && sizes.Length > 0)
                    {
                        // Get the largest available size (highest resolution)
                        var largestSize = sizes.OrderByDescending(s => s.Width * s.Height).First();
                        _cameraWidth = largestSize.Width;
                        _cameraHeight = largestSize.Height;
                        InfoMessage?.Invoke($"Camera resolution detected: {_cameraWidth}x{_cameraHeight}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            InfoMessage?.Invoke($"Could not detect camera capabilities, using defaults: {ex.Message}");
        }
    }

    // CameraDevice.StateCallback implementation
    private class CameraStateCallback : CameraDevice.StateCallback
    {
        private readonly ArCoreService _service;
        public CameraStateCallback(ArCoreService service) => _service = service;

        public override void OnOpened(CameraDevice camera)
        {
            _service._cameraDevice = camera;

            try
            {
                camera.CreateCaptureSession(
                    [_service._imageReader!.Surface!],
                    new CaptureSessionCallback(_service),
                    _service._backgroundHandler
                );
            }
            catch (Exception ex)
            {
                _service.InfoMessage?.Invoke($"Camera setup error: {ex.Message}");
            }
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            _service._cameraDevice = null;
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera.Close();
            _service._cameraDevice = null;
        }
    }

    // Listener for ImageReader
    private class ImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly Action<ImageReader?> _onImageAvailable;
        public ImageAvailableListener(Action<ImageReader?> onImageAvailable) => _onImageAvailable = onImageAvailable;
        public void OnImageAvailable(ImageReader? reader) => _onImageAvailable(reader);
    }

    // CameraCaptureSession.CaptureCallback implementation
    private class CameraCaptureCallbackImpl : CameraCaptureSession.CaptureCallback
    {
        private readonly Action _onCaptureCompleted;
        public CameraCaptureCallbackImpl(Action onCaptureCompleted) => _onCaptureCompleted = onCaptureCompleted;
        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        {
            base.OnCaptureCompleted(session, request, result);
            _onCaptureCompleted();
        }
    }

    // CameraCaptureSession.StateCallback implementation
    private class CaptureSessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly ArCoreService _service;
        public CaptureSessionCallback(ArCoreService service) => _service = service;

        public override void OnConfigured(CameraCaptureSession session)
        {
            var buildRequest = _service._cameraDevice!.CreateCaptureRequest(CameraTemplate.StillCapture);
            buildRequest.AddTarget(_service._imageReader!.Surface!);

            // Enhanced settings for best capture quality
            buildRequest.Set(CaptureRequest.ControlAfMode!, (int)ControlAFMode.ContinuousPicture);
            buildRequest.Set(CaptureRequest.ControlAeMode!, (int)ControlAEMode.On);
            buildRequest.Set(CaptureRequest.ControlAwbMode!, (int)ControlAwbMode.Auto);
            buildRequest.Set(CaptureRequest.ControlMode!, (int)ControlMode.Auto);
            buildRequest.Set(CaptureRequest.ControlCaptureIntent!, (int)ControlCaptureIntent.StillCapture);
            buildRequest.Set(CaptureRequest.JpegQuality!, (sbyte)100); // Maximum JPEG quality
            buildRequest.Set(CaptureRequest.JpegOrientation!, 90);
            buildRequest.Set(CaptureRequest.NoiseReductionMode!, (int)NoiseReductionMode.HighQuality);
            buildRequest.Set(CaptureRequest.EdgeMode!, (int)EdgeMode.HighQuality);
            buildRequest.Set(CaptureRequest.ColorCorrectionMode!, (int)ColorCorrectionMode.HighQuality);
            buildRequest.Set(CaptureRequest.ControlVideoStabilizationMode!, (int)ControlVideoStabilizationMode.Off);

            session.Capture(buildRequest.Build(),
                new CameraCaptureCallbackImpl(() =>
                {
                    _service.InfoMessage?.Invoke("High-quality capture completed.");
                }),
                _service._backgroundHandler
            );
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
        }
    }

    private record ProjectInfo
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public DateTime Timestamp { get; init; }
        public string FolderPath { get; init; } = string.Empty;
        public string TaskId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public string ViewUrl { get; init; } = string.Empty;
        public string DownloadUrl { get; init; } = string.Empty;
    }
}