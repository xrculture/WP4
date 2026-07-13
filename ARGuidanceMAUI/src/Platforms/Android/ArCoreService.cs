using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.Opengl;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using ARGuidanceMAUI.Models;
using ARGuidanceMAUI.Services;
using Google.AR.Core;
using Google.AR.Core.Exceptions;
using Java.Net;
using Java.Nio;
using Java.Util;
using Javax.Microedition.Khronos.Opengles; // OpenGL ES + EGL types (for IGL10, EGLConfig)
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Xamarin.Google.Crypto.Tink.Shaded.Protobuf;
using ArFrame = Google.AR.Core.Frame;
using Image = global::Android.Media.Image;

namespace ARGuidanceMAUI.Platforms.Android;

public class ArCoreService : Java.Lang.Object, IArPlatformService, GLSurfaceView.IRenderer
{
    public event Action<GuidanceState>? GuidanceUpdated;
    public event Func<CapturePackage, Task>? CaptureReady;
    public event Action<ArDebugTelemetry>? DebugUpdated;

    private readonly Context _ctx;
    private Session? _session;
    private GLSurfaceView? _glView;
    private int _cameraTextureId;

    // Surface/display
    private int _surfaceWidth = 0;
    private int _surfaceHeight = 0;

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
    private bool _cameraTracking = false;
    private bool _capturing = false;
    private bool _readyToCapture = false;
    private int _captures = 0;
    private const float MinPointConfidence = 0.05f;

    private CameraCaptureSession? _captureSession;
    private SemaphoreSlim _captureSemaphore = new SemaphoreSlim(1, 1);
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

    // Logger
    private readonly Serilog.ILogger _logger;

    private bool _isMediaTekDevice = false;
    private bool _sessionInitialized = false;
    private object _sessionLock = new object();

    public ArCoreService()
    {
        _ctx = global::Android.App.Application.Context!;
        _logger = Log.Logger.ForContext<ArCoreService>();
        _3DReconstructionServerUrl = "http://xrculture.rdf.bg:30026/";

        // Detect MediaTek early
        _isMediaTekDevice = IsMediaTekDevice();

        LogDeviceInfo();
        LogMemoryUsage();
        LogBatteryAndThermalInfo();
        DetectCameraCapabilities();
    }

    private bool IsMediaTekDevice()
    {
        var manufacturer = global::Android.OS.Build.Manufacturer?.ToLowerInvariant() ?? "";
        var hardware = global::Android.OS.Build.Hardware?.ToLowerInvariant() ?? "";
        var soc = global::Android.OS.Build.SocManufacturer?.ToLowerInvariant() ?? "";

        return manufacturer.Contains("xiaomi") &&
               (hardware.Contains("mt") || soc.Contains("mediatek") || soc.Contains("mt"));
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

    public Task Options()
    {
        if (Application.Current?.MainPage == null) return Task.CompletedTask;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var actions = new List<string>() { "New Project", "Projects", "Server URL", "Share Log" };
                var action = await Application.Current.MainPage.DisplayActionSheetAsync(
                    $"Options",
                    "Cancel",
                    null,
                    actions.ToArray()
                );

                if (action == "New Project")
                {
                    ResetDataStructures();
                    CreateNewProject();
                }
                else if (action == "Projects")
                {
                    await ShowProjectsUI();
                }
                else if (action == "Server URL")
                {
                    await ShowServerUI();
                }
                else if (action == "Share Log")
                {
                    await ShareLogAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error displaying options.");
            }
        });

        return Task.CompletedTask;
    }

    public async Task ShowProjectsUI()
    {
        try
        {
            var projects = await GetAllProjectsAsync();

            if (projects.Count == 0)
            {
                _logger.Information("No projects found.");
                return;
            }

            // Show project list to user
            var projectNames = projects.Select(p => $"{p.Name} - {p.Timestamp:yyyy-MM-dd HH:mm}").ToArray();

            if (Application.Current?.MainPage != null)
            {
                var selectedProject = await Application.Current.MainPage.DisplayActionSheetAsync(
                    "Select Project",
                    "Cancel",
                    null,
                    projectNames
                );

                if (!string.IsNullOrEmpty(selectedProject) && selectedProject != "Cancel")
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
            _logger.Error(ex, "Error loading projects.");
        }
    }

    public async Task ShowServerUI()
    {
        try
        {
            if (Application.Current?.MainPage != null)
            {
                var serverUrl = LoadServerUrlFromSettings();
                serverUrl = await Application.Current.MainPage.DisplayPromptAsync(
                    "Server URL",
                    "Enter server URL:",
                    initialValue: serverUrl,
                    accept: "OK",
                    cancel: "Cancel",
                    placeholder: "https://example.com",
                    maxLength: 200
                );

                if (string.IsNullOrEmpty(serverUrl) || (serverUrl == "Cancel"))
                {
                    return;
                }

                if (IsValidHttpUrl(serverUrl))
                {
                    SaveServerUrlToSettings(serverUrl);
                }
                else 
                { 
                    await ShowMessageAsync("Invalid URL", "Please enter a valid HTTP or HTTPS URL."); 
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error setting up server.");
        }
    }

    private bool IsValidHttpUrl(string url)
    {
        return !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult)
            && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
    }

    private string LoadServerUrlFromSettings()
    {
        try
        {
            var settingsDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "settings");
            if (!Directory.Exists(settingsDir))
            {
                Directory.CreateDirectory(settingsDir);
            }

            var settingsFile = System.IO.Path.Combine(settingsDir, "appsettings.xml");
            if (File.Exists(settingsFile))
            {
                var xmlContent = File.ReadAllText(settingsFile);
                var doc = new XmlDocument();
                doc.LoadXml(xmlContent);

                var serverUrlNode = doc.SelectSingleNode("//ServerUrl");
                if (serverUrlNode != null)
                {
                    return serverUrlNode.InnerText;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading server URL from settings.");
        }

        return _3DReconstructionServerUrl;
    }

    private string SaveServerUrlToSettings(string serverUrl)
    {
        try
        {
            var settingsDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "settings");
            if (!Directory.Exists(settingsDir))
            {
                Directory.CreateDirectory(settingsDir);
            }

            var settingsFile = System.IO.Path.Combine(settingsDir, "appsettings.xml");
            StringBuilder settingsXML = new();
            settingsXML.Append("<AppSettings>");
            settingsXML.Append("<ServerUrl><![CDATA[");
            settingsXML.Append(serverUrl);
            settingsXML.Append("]]></ServerUrl>");
            settingsXML.Append("</AppSettings>");
            File.WriteAllText(settingsFile, settingsXML.ToString());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error saving server URL to settings.");
        }

        return serverUrl;
    }

    public async Task ShareLogAsync()
    {
        try
        {
            var logsDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logs");

            if (!Directory.Exists(logsDir))
            {
                await ShowMessageAsync("Share Log", "No log files found.");
                return;
            }

            // Get all log files
            var logFiles = Directory.GetFiles(logsDir, "*.txt");

            if (logFiles.Length == 0)
            {
                await ShowMessageAsync("Share Log", "No log files found.");
                return;
            }

            // Get the most recent log file
            var latestLogFile = logFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Share Log File",
                File = new ShareFile(latestLogFile)
            });

            _logger.Information("Log file shared: {LogFile}", latestLogFile);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to share log file");
            await ShowMessageAsync("Error", $"Failed to share log file: {ex.Message}");
        }
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
            _logger.Error(ex, "Error retrieving projects.");
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
            _logger.Error(ex, "Error reading project XML.");
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

        var action = await Application.Current.MainPage.DisplayActionSheetAsync(
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

            _logger.Information("Starting 3D model creation...");

            var context = global::Android.App.Application.Context;
            if (context?.ContentResolver == null)
            {
                _logger.Error("Content resolver not available.");
                return;
            }

            // Query MediaStore for images in the project folder
            var imageUris = await GetProjectImagesAsync(project.FolderPath);

            if (imageUris.Count == 0)
            {
                await ShowMessageAsync("3D Model Creation", "No images found in the project folder.");
                return;
            }

            _logger.Information("Found {ImageCount} images for project '{ProjectName}'.", imageUris.Count, project.Name);

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

                _logger.Information("Created zip file: {ZipFilePath} with {ImageCount} images.", zipFilePath, imageUris.Count);

                var taskId = await Create3DModelTask(project.Name, zipFilePath);
                if (!string.IsNullOrEmpty(taskId))
                {
                    _logger.Information("3D model creation task started. Task ID: {TaskId}", taskId);

                    var updatedProject = project with { TaskId = taskId, Status = "Pending" };
                    await UpdateProject(updatedProject);

                    ResetDataStructures();
                }
                else
                {
                    _logger.Error("Failed to start 3D model creation task.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error creating zip file.");
            }
            finally
            {
                if (File.Exists(zipFilePath))
                {
                    try
                    {
                        File.Delete(zipFilePath);
                        _logger.Information("Temporary zip file deleted.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error deleting temporary zip file.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error creating 3D model.");
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
            _logger.Error(ex, "Failed to create 3D model.");
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
                    _logger.Information("{Title}: {Message}", title, message);
                }
            }
            catch
            {
                _logger.Warning("Failed to show message dialog.");
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
                _logger.Warning(ex, $"HTTP request failed on attempt {retryCount + 1}. Retrying...");

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
            _logger.Error(ex, "Error querying images.");
        }

        return imageUris;
    }

    private async Task CheckModelStatusAsync(ProjectInfo project)
    {
        try
        {
            _logger.Information("Checking model status...");

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
                        _logger.Error("Unable to parse response as JSON or XML. Response: {Response}", responseString);
                        await ShowMessageAsync("Error", "Unable to parse response as JSON or XML.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error checking model status.");
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

                    _logger.Information("Project created: {ProjectName} at {FolderPath}", result.Name, CurrentProjectFolder);
                }
                else
                {
                    _logger.Information("Project creation cancelled.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error creating project.");
                _logger.Error("Error creating project: {Message}", ex.Message);
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
                _logger.Error("Content resolver not available.");
                return;
            }

            // Try to find existing file first
            var existingUri = await FindProjectXmlUriAsync(relativePath);

            global::Android.Net.Uri? uri;

            if (existingUri != null)
            {
                // File exists - update it
                uri = existingUri;
                _logger.Information("Updating existing project XML at: {RelativePath}", relativePath);
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
                    _logger.Error("Failed to create XML file via MediaStore at: {RelativePath}", relativePath);
                    return;
                }
                _logger.Information("Creating new project XML at: {RelativePath}", relativePath);
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

            _logger.Information("Project XML saved successfully at: {RelativePath}", relativePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error saving project XML.");
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

            _logger.Information("No existing project XML found at: {RelativePath}", relativePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error finding project XML URI.");
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

        _logger.Information("Project creation cancelled by user.");
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
        CurrentProjectName = string.Empty;
        CurrentProjectFolder = string.Empty;
        _accumCentroid = new float[3];
        _centroidCount = 0;
        _currentPose = null;
        _currentYaw = 0f;
        _featuresCount = 0;
        _poses = new();
        _yaws = new();
        _capturing = false;
        _readyToCapture = false;
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
                    _logger.Information("Checking ARCore availability...");

                    var availability = ArCoreApk.Instance.CheckAvailability(_ctx);
                    _logger.Information("ARCore availability status: {Availability}", availability);

                    if (availability.IsUnknown)
                    {
                        await Task.Delay(200);
                        availability = ArCoreApk.Instance.CheckAvailability(_ctx);
                        _logger.Information("ARCore availability after wait: {Availability}", availability);
                    }

                    if (!availability.IsSupported)
                    {
                        GuidanceUpdated?.Invoke(new GuidanceState
                        {
                            Hint = $"ARCore not supported. Status: {availability}"
                        });
                        return;
                    }

                    _logger.Information("ARCore is available. Proceeding to create session.");
                    //_logger.Information("Creating ARCore session...");

                    //_session = new Session(_ctx, [Session.Feature.SharedCamera]);

                    //_logger.Information("ARCore session created successfully.");
                }
                catch (Java.Lang.ClassNotFoundException ex)
                {
                    _logger.Error(ex, "ARCore class not found (ProGuard issue).");
                    GuidanceUpdated?.Invoke(new GuidanceState
                    {
                        Hint = $"ARCore class not found (ProGuard issue): {ex.Message}"
                    });
                    return;
                }
                catch (UnavailableException e)
                {
                    _logger.Error(e, "ARCore unavailable.");
                    GuidanceUpdated?.Invoke(new GuidanceState
                    {
                        Hint = $"ARCore unavailable: {e.Message}"
                    });
                    return;
                }
                catch (Exception e)
                {
                    _logger.Error(e, "ARCore error.");
                    GuidanceUpdated?.Invoke(new GuidanceState
                    {
                        Hint = $"ARCore error: {e.GetType().Name} - {e.Message}"
                    });
                    return;
                }

                CreateSession();

                StartBackgroundThread();
            }

            try
            {
                _session?.Resume();
                _glView?.OnResume();

                if (_session != null && _cameraTextureId != 0)
                    _session.SetCameraTextureName(_cameraTextureId);

                _logger.Information("AR session resumed successfully.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error resuming AR session.");
            }
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error starting AR session.");
            GuidanceUpdated?.Invoke(new GuidanceState { Hint = $"Error starting AR: {e.Message}" });
        }
    }

    private void CreateSession()
    {
        try
        {
            _logger.Information("Creating ARCore session...");

            // CREATE SESSION FIRST
            _session = new Session(_ctx);

            // Then configure it
            var config = new Config(_session);
            config.SetUpdateMode(Config.UpdateMode.LatestCameraImage);
            config.SetFocusMode(/*_isMediaTekDevice ? Config.FocusMode.Fixed : */Config.FocusMode.Auto);
            config.SetPlaneFindingMode(Config.PlaneFindingMode.Disabled);
            config.SetLightEstimationMode(Config.LightEstimationMode.Disabled);
            config.SetDepthMode(Config.DepthMode.Disabled);
            config.SetCloudAnchorMode(Config.CloudAnchorMode.Disabled);

            // Apply config
            _session.Configure(config);

            // Select camera config
            var filter = new CameraConfigFilter(_session);
            filter.SetTargetFps(EnumSet.Of(CameraConfig.TargetFps.TargetFps30));
            filter.SetFacingDirection(CameraConfig.FacingDirection.Back);

            var cameraConfigList = _session.GetSupportedCameraConfigs(filter);
            if (cameraConfigList != null && cameraConfigList.Count > 0)
            {
                var highestResConfig = cameraConfigList
                    .OrderBy(c => c.ImageSize.Width * c.ImageSize.Height)
                    .Last();

                _session.CameraConfig = highestResConfig;

                _logger.Information("ARCore camera config: {Width}x{Height} @ {Fps}fps",
                    highestResConfig.ImageSize.Width,
                    highestResConfig.ImageSize.Height,
                    highestResConfig.FpsRange);
            }

            _logger.Information("✓ ARCore session created successfully");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create ARCore session");
            throw;
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
        try
        {
            _glView?.OnPause();
            _session?.Pause();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error stopping AR session.");
        }
    }

    private DateTime _lastCaptureTime = DateTime.MinValue;

    public async void RequestCapture()
    {
        await CaptureHighResPhoto();
        return;
        // Block captures if thermal state is Critical or worse
        if (_currentThermalStatus >= 4)
        {
            _logger.Warning("Capture blocked: thermal status too high ({Status})", _currentThermalStatus);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                GuidanceUpdated?.Invoke(new GuidanceState
                {
                    Hint = "Device too hot! Please wait for it to cool down."
                });
            });
            return;
        }

        if (!_cameraTracking)
        {
            _logger.Information("Cannot capture: AR tracking not ready.");
            return;
        }

        if (string.IsNullOrEmpty(CurrentProjectFolder))
        {
            _logger.Information("No active project. Create a new project first.");
            return;
        }

        if (_currentPose == null)
        {
            _logger.Information("AR tracking not ready. Move the device to initialize.");
            return;
        }

        // #todo: Check for sufficient feature points
        //if (_featuresCount == 0)
        //{
        //    _logger.Information("Not enough feature points detected. Move around to add more features.");
        //    return Task.CompletedTask;
        //}

        if (_capturing)
        {
            _logger.Information("Already capturing. Please wait.");
            return;
        }

        // Add minimum delay between captures to reduce CPU/battery load
        var timeSinceLastCapture = DateTime.Now - _lastCaptureTime;
        var minInterval = _thermalThrottlingActive ? 2000 : 500;

        // Add extra delay for severe thermal conditions
        if (_currentThermalStatus >= 3)
        {
            _logger.Warning("Thermal throttling active (Severe+). Adding cooldown delay...");

            int extraDelayMs = _currentThermalStatus switch
            {
                3 => 3000,   // Severe: +3 seconds (total 5s between captures)
                4 => 5000,   // Critical: +5 seconds (total 7s)
                >= 5 => 8000, // Emergency+: +8 seconds (total 10s)
                _ => 0
            };

            minInterval += extraDelayMs;

            // Show warning to user for Critical+
            if (_currentThermalStatus >= 4)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    GuidanceUpdated?.Invoke(new GuidanceState
                    {
                        Hint = $"Device temperature critical. Waiting {minInterval / 1000}s to cool down..."
                    });
                });
            }
        }

        if (timeSinceLastCapture.TotalMilliseconds < minInterval)
        {
            var waitTime = minInterval - (int)timeSinceLastCapture.TotalMilliseconds;
            _logger.Information("Waiting {WaitMs}ms before next capture (thermal protection)", waitTime);
            await Task.Delay(waitTime);
        }

        _lastCaptureTime = DateTime.Now;

        // Use semaphore to prevent concurrent captures
        if (!await _captureSemaphore.WaitAsync(0))
        {
            _logger.Information("Capture already in progress (semaphore locked).");
            return;
        }

        _lastCaptureTime = DateTime.Now; // Record capture time
        _logger.Information("Semaphore acquired, starting capture...");

        try
        {
            _capturing = true;

            // Ensure previous resources are cleaned up
            CleanupCameraResources();

            // **INCREASE delay for MIUI cloud camera service**
            int cleanupDelay = _isMediaTekDevice ? 600 : 50; // 600ms for MIUI, 50ms for others
            _logger.Information("Waiting {DelayMs}ms for camera cleanup...", cleanupDelay);
            await Task.Delay(cleanupDelay);

            var cameraManager = _ctx.GetSystemService(Context.CameraService) as CameraManager;
            if (cameraManager == null)
            {
                _logger.Warning("Camera manager not available.");
                _capturing = false;
                _captureSemaphore.Release();
                return;
            }

            var cameraIdList = cameraManager.GetCameraIdList();
            if (cameraIdList == null || cameraIdList.Length == 0)
            {
                _logger.Warning("No camera found on the device.");
                _capturing = false;
                _captureSemaphore.Release();
                return;
            }

            string cameraId = cameraIdList.First();

            var characteristics = cameraManager.GetCameraCharacteristics(cameraId);
            if (characteristics == null)
            {
                _logger.Error("Cannot get camera characteristics for camera ID: {CameraId}", cameraId);
                _capturing = false;
                _captureSemaphore.Release();
                return;
            }

            // Use adaptive resolution
            var (captureWidth, captureHeight) = GetOptimalCaptureSize();
            _logger.Information("Using capture resolution: {Width}x{Height} (thermal status: {Status})",
                captureWidth, captureHeight, _currentThermalStatus);

            _imageReader = ImageReader.NewInstance(captureWidth, captureHeight, ImageFormatType.Jpeg, 3);
            _imageReader.SetOnImageAvailableListener(new ImageAvailableListener(OnImageAvailable), _backgroundHandler);

            // **INCREASE delay for MIUI cloud camera service**
            int openDelay = _isMediaTekDevice ? 800 : 200; // 800ms for MIUI, 200ms for others
            _logger.Information("Waiting {DelayMs}ms before opening camera...", openDelay);

            _backgroundHandler?.PostDelayed(async () =>
            {
                try
                {
                    if (_isMediaTekDevice)
                    {
                        var isAvailable = await WaitForCameraAvailability(cameraManager, cameraId, 2000);
                        if (!isAvailable)
                        {
                            _logger.Error("Camera not available, aborting capture");
                            _capturing = false;
                            _captureSemaphore.Release();
                            return;
                        }
                    }
                    cameraManager.OpenCamera(cameraId, new CameraStateCallback(this), _backgroundHandler);
                }
                catch (Exception ex)
                {
                    _capturing = false;
                    _logger.Error(ex, "Failed to open camera.");
                    _captureSemaphore.Release();
                }
            }, openDelay);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Camera setup error.");
            _capturing = false;
            _captureSemaphore.Release();
        }
    }

    // Add these fields at the top of the class (around line 75):
    private CameraManager.AvailabilityCallback? _cameraAvailabilityCallback;
    private TaskCompletionSource<bool>? _cameraAvailableTcs;
    private string? _waitingForCameraId;

    // Replace the existing WaitForCameraAvailability method (around line 1629):
    private async Task<bool> WaitForCameraAvailability(CameraManager cameraManager, string cameraId, int maxWaitMs = 3000)
    {
        _logger.Information("Waiting for camera {CameraId} to become available via callback...", cameraId);

        _waitingForCameraId = cameraId;
        _cameraAvailableTcs = new TaskCompletionSource<bool>();

        // Register callback if not already registered
        if (_cameraAvailabilityCallback == null)
        {
            _cameraAvailabilityCallback = new CameraAvailabilityCallbackHandler(this);
            cameraManager.RegisterAvailabilityCallback(_cameraAvailabilityCallback, _backgroundHandler);
            _logger.Information("Registered camera availability callback");
        }

        // Wait for callback or timeout
        var timeoutTask = Task.Delay(maxWaitMs);
        var completedTask = await Task.WhenAny(
            _cameraAvailableTcs.Task,
            timeoutTask
        );

        if (completedTask == _cameraAvailableTcs.Task)
        {
            _logger.Information("✓ Camera {CameraId} became available", cameraId);
            return true;
        }
        else
        {
            _logger.Warning("⏱ Timeout waiting for camera {CameraId} (waited {MaxWaitMs}ms)", cameraId, maxWaitMs);

            // Fallback: try retry logic
            _logger.Information("Falling back to retry logic...");
            return await TryOpenCameraWithRetry(cameraManager, cameraId);
        }
    }

    // Add this helper method for retry fallback:
    private async Task<bool> TryOpenCameraWithRetry(CameraManager cameraManager, string cameraId, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.Information("Retry attempt {Attempt}/{MaxRetries} for camera {CameraId}", attempt, maxRetries, cameraId);

                // Try to get characteristics
                var characteristics = cameraManager.GetCameraCharacteristics(cameraId);
                if (characteristics != null)
                {
                    _logger.Information("✓ Camera characteristics available on attempt {Attempt}", attempt);
                    await Task.Delay(200); // Small extra delay for safety
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Retry attempt {Attempt} failed", attempt);
            }

            if (attempt < maxRetries)
            {
                int delay = 300 * attempt; // 300ms, 600ms, 900ms
                _logger.Information("Waiting {DelayMs}ms before next retry...", delay);
                await Task.Delay(delay);
            }
        }

        return false;
    }

    // Add the callback handler class at the end of the ArCoreService class (before the last closing brace):
    private class CameraAvailabilityCallbackHandler : CameraManager.AvailabilityCallback
    {
        private readonly ArCoreService _service;

        public CameraAvailabilityCallbackHandler(ArCoreService service)
        {
            _service = service;
        }

        public override void OnCameraAvailable(string cameraId)
        {
            _service._logger.Information("📷 OnCameraAvailable callback fired for camera {CameraId}", cameraId);

            if (_service._waitingForCameraId == cameraId)
            {
                _service._cameraAvailableTcs?.TrySetResult(true);
            }
        }

        public override void OnCameraUnavailable(string cameraId)
        {
            _service._logger.Information("🚫 OnCameraUnavailable callback fired for camera {CameraId}", cameraId);
        }
    }

    // Update CleanupCameraResources to trigger the availability callback:
    private void CleanupCameraResources()
    {
        try
        {
            // Close capture session
            if (_captureSession != null)
            {
                try
                {
                    _captureSession.StopRepeating();
                    _captureSession.AbortCaptures();
                    Thread.Sleep(100); // Give time for abort
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error stopping capture session.");
                }
                finally
                {
                    _captureSession = null;
                }
            }

            // Close camera device
            if (_cameraDevice != null)
            {
                try
                {
                    _cameraDevice.Close(); // This should trigger OnCameraAvailable callback
                    _logger.Information("Camera device closed - waiting for availability callback...");

                    if (_isMediaTekDevice)
                    {
                        Thread.Sleep(300); // MIUI needs extra time
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error closing camera device.");
                }
                finally
                {
                    _cameraDevice = null;
                }
            }

            // Close image reader
            if (_imageReader != null)
            {
                try
                {
                    _imageReader.Close();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error closing image reader.");
                }
                finally
                {
                    _imageReader = null;
                }
            }

            // Resume ARCore session after Camera2 cleanup
            try
            {
                if (_session != null)
                {
                    if (_isMediaTekDevice)
                    {
                        Thread.Sleep(200);
                    }

                    _session.Resume();
                    _logger.Information("ARCore session resumed after camera cleanup");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error resuming ARCore session after cleanup");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in CleanupCameraResources.");
        }
    }

    // Don't forget to unregister callback on dispose:
    //public void Dispose()
    //{
    //    try
    //    {
    //        if (_cameraAvailabilityCallback != null && _ctx != null)
    //        {
    //            var cameraManager = _ctx.GetSystemService(Context.CameraService) as CameraManager;
    //            if (cameraManager != null)
    //            {
    //                cameraManager.UnregisterAvailabilityCallback(_cameraAvailabilityCallback);
    //                _logger.Information("Unregistered camera availability callback");
    //            }
    //            _cameraAvailabilityCallback = null;
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.Warning(ex, "Error unregistering camera callback");
    //    }

    //    // ... rest of cleanup ...
    //}

    private (int width, int height) GetOptimalCaptureSize()
    {
        // Default based on detected capabilities
        if (!_thermalThrottlingActive || _currentThermalStatus < 2)
        {
            return (_cameraWidth, _cameraHeight);
        }

        // Reduce resolution during thermal throttling
        return _currentThermalStatus switch
        {
            2 => (_cameraWidth, _cameraHeight),  // Moderate: keep detected size
            3 => (1920, 1080),                    // Severe: reduce to Full HD
            4 => (1280, 720),                     // Critical: reduce to HD
            >= 5 => (960, 540),                   // Emergency+: reduce to low resolution
            _ => (_cameraWidth, _cameraHeight)
        };
    }

    // GLSurfaceView.IRenderer
    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        _logger.Information("OnSurfaceCreated called");

        // Initialize GL resources synchronously
        InitializeGLResources();

        // Create camera texture if session exists
        if (_session != null && _cameraTextureId == 0)
        {
            int[] textures = new int[1];
            GLES20.GlGenTextures(1, textures, 0);
            _cameraTextureId = textures[0];

            GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _cameraTextureId);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);

            _session.SetCameraTextureName(_cameraTextureId);
            _logger.Information("✓ Camera texture created and bound");
        }
    }

    private async Task InitializeArCoreSessionAsync()
    {
        try
        {
            // Add delay for MediaTek HAL stabilization
            await Task.Delay(_isMediaTekDevice ? 500 : 100);

            lock (_sessionLock)
            {
                if (_sessionInitialized) return;

                InitializeArCoreSession();
                _sessionInitialized = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize ARCore session asynchronously");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Application.Current?.MainPage?.DisplayAlert(
                    "Camera Error",
                    "ARCore failed to initialize. Please restart the app.",
                    "OK"
                );
            });
        }
    }

    private void InitializeArCoreSession()
    {
        try
        {
            _logger.Information("Creating ARCore session...");

            // CRITICAL FIX: Create session FIRST, then configure it
            _session = new Session(_ctx, [Session.Feature.SharedCamera]);

            // Now create config with the existing session
            var config = new Config(_session);
            if (_isMediaTekDevice)
            {
                // Reduce camera load
                config.SetUpdateMode(Config.UpdateMode.LatestCameraImage);
                config.SetFocusMode(Config.FocusMode.Fixed); // Avoid autofocus on problematic devices
            }

            _session.Configure(config);

            // Create camera texture
            int[] textures = new int[1];
            GLES20.GlGenTextures(1, textures, 0);
            _cameraTextureId = textures[0];

            GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _cameraTextureId);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);

            _session.SetCameraTextureName(_cameraTextureId);

            _logger.Information("✓ ARCore session created successfully");
        }
        catch (UnavailableException ex)
        {
            _logger.Error(ex, "ARCore unavailable");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ARCore session creation failed");
            throw;
        }
    }

    private void ConfigureCameraForMediaTek()
    {
        if (!MainActivity.IsMediaTekHardware() || _session == null)
            return;

        try
        {
            var config = new Config(_session);

            // Use lowest settings for MediaTek
            var cameraConfigFilter = new CameraConfigFilter(_session);
            cameraConfigFilter.SetTargetFps(EnumSet.Of(CameraConfig.TargetFps.TargetFps30)); // 30 FPS max
            cameraConfigFilter.SetFacingDirection(CameraConfig.FacingDirection.Back);

            var cameraConfigs = _session.GetSupportedCameraConfigs(cameraConfigFilter);
            if (cameraConfigs.Count > 0)
            {
                // Pick LOWEST resolution for stability
                var lowestConfig = cameraConfigs
                    .OrderBy(c => c.ImageSize.Width * c.ImageSize.Height)
                    .First();

                _session.CameraConfig = lowestConfig;
                _session.Configure(config);

                _logger.Information($"MediaTek camera config: {lowestConfig.ImageSize.Width}x{lowestConfig.ImageSize.Height}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to configure camera for MediaTek");
        }
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        try
        {
            GLES20.GlViewport(0, 0, width, height);

            if ((_session != null) && (width > 0) && (height > 0) && (_surfaceWidth != width) && (_surfaceHeight != height))
            {
                _surfaceWidth = width;
                _surfaceHeight = height;
                _session.SetDisplayGeometry((int)GetDisplayRotation(), width, height);
                _logger.Information("Surface changed: width={Width}, height={Height}, rotation={Rotation}", width, height, GetDisplayRotation());
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in OnSurfaceChanged.");
        }

    }

    public void OnDrawFrame(IGL10? gl)
    {
        // Check if camera is still ready (especially important for MediaTek)
        if (MainActivity.IsMediaTekHardware() && !MainActivity.IsCameraReady())
        {
            return; // Skip this frame
        }

        GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);

        try
        {
            _cameraTracking = false;

            if (_session == null)
            {
                return;
            }

            // Skip if already capturing
            if (_capturing)
            {
                Thread.Sleep(50);
                GuidanceUpdated?.Invoke(new GuidanceState { Hint = "Processing capture..." });
                return;
            }

            ArFrame frame;
            try
            {
                frame = _session.Update();
            }
            catch (SessionPausedException)
            {
                return;
            }
            catch (Java.Lang.IllegalStateException ex)
            {
                _logger.Warning(ex, "AR session not ready");
                return;
            }

            var cam = frame.Camera;
            _currentPose = cam.Pose;

            // **CAPTURE LOGIC - CHECK FLAG**
            bool shouldCapture = false;
            lock (_captureLock)
            {
                if (_requestCapture && !_capturing)
                {
                    _requestCapture = false;
                    _capturing = true;
                    shouldCapture = true;
                }
            }

            if (shouldCapture)
            {
                // Capture synchronously on GL thread, process async
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CaptureFromArFrameAsync(frame);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error in capture task");
                    }
                    finally
                    {
                        lock (_captureLock)
                        {
                            _capturing = false;
                        }
                    }
                });
            }


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

            _cameraTracking = true;


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
        catch (SessionPausedException)
        {
            // Silently ignore - session will resume on next frame
            return;
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error updating AR session.");
            GuidanceUpdated?.Invoke(new GuidanceState { Hint = $"Error: {e.Message}" });
        }
    }

    private async Task CaptureFromArFrameAsync(ArFrame frame)
    {
        Image? arImage = null;
        byte[]? jpegBytes = null;

        try
        {
            _logger.Information("Acquiring ARCore camera image...");

            // Acquire image
            arImage = frame.AcquireCameraImage();
            if (arImage == null)
            {
                _logger.Warning("Could not acquire camera image from ARCore");
                return;
            }

            _logger.Information("Converting YUV to JPEG ({Width}x{Height})...", arImage.Width, arImage.Height);

            // Convert to JPEG (this reads the image data)
            jpegBytes = ConvertYuvToJpeg(arImage);

            _logger.Information("Captured ARCore image: {Size} bytes", jpegBytes.Length);
        }
        catch (NotSupportedException ex)
        {
            _logger.Warning(ex, "Image format not supported");
            return;
        }
        catch (ResourceExhaustedException ex)
        {
            _logger.Warning(ex, "ARCore image buffer pool exhausted - capture too fast");
            return;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error acquiring/converting ARCore image");
            return;
        }
        finally
        {
            // **CRITICAL: Dispose image immediately to return it to ARCore's buffer pool**
            if (arImage != null)
            {
                try
                {
                    arImage.Close();
                    arImage.Dispose();
                    _logger.Information("ARCore image released");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error disposing ARCore image");
                }
            }
        }

        // Now process the JPEG bytes (image is already released)
        if (jpegBytes != null && CaptureReady != null)
        {
            var package = new CapturePackage
            {
                JpegBytes = jpegBytes,
                MetadataJson = "",
                FileBaseName = $"cap_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            };

            try
            {
                await Task.WhenAll(
                    CaptureReady.GetInvocationList()
                        .Cast<Func<CapturePackage, Task>>()
                        .Select(handler => handler(package))
                );

                _logger.Information("Image save completed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in CaptureReady handler");
            }
        }

        // Update poses
        if (_poses.Count > 100)
        {
            _poses.RemoveAt(0);
            _yaws.RemoveAt(0);
        }

        if (_currentPose != null)
        {
            _poses.Add(_currentPose);
            _yaws.Add(_currentYaw);
        }

        _captures++;

        LogMemoryUsage();
        LogBatteryAndThermalInfo();
    }

    private byte[] ConvertYuvToJpeg(Image arImage)
    {
        int width = arImage.Width;
        int height = arImage.Height;

        var planes = arImage.GetPlanes();
        if (planes == null || planes.Length < 3)
        {
            throw new NotSupportedException("ARCore image does not have required planes");
        }

        // Get all three planes
        var yPlane = planes[0];
        var uPlane = planes[1];
        var vPlane = planes[2];

        var yBuffer = yPlane.Buffer;
        var uBuffer = uPlane.Buffer;
        var vBuffer = vPlane.Buffer;

        if (yBuffer == null || uBuffer == null || vBuffer == null)
        {
            throw new NotSupportedException("One or more plane buffers are null");
        }

        // Get plane properties
        int yRowStride = yPlane.RowStride;
        int uvRowStride = uPlane.RowStride;
        int uvPixelStride = uPlane.PixelStride;

        // NV21 format: YYYYYYYY VUVUVU (Y plane + interleaved VU)
        int ySize = width * height;
        int uvSize = width * height / 2;
        byte[] nv21 = new byte[ySize + uvSize];

        // **STEP 1: Copy Y plane**
        yBuffer.Rewind();
        if (yRowStride == width)
        {
            // No padding - direct copy
            yBuffer.Get(nv21, 0, ySize);
        }
        else
        {
            // Has padding - copy row by row
            for (int row = 0; row < height; row++)
            {
                yBuffer.Position(row * yRowStride);
                yBuffer.Get(nv21, row * width, width);
            }
        }

        // **STEP 2: Interleave U and V planes into NV21 format (VUVUVU...)**
        uBuffer.Rewind();
        vBuffer.Rewind();

        int uvWidth = width / 2;
        int uvHeight = height / 2;
        int nv21Index = ySize;

        for (int row = 0; row < uvHeight; row++)
        {
            for (int col = 0; col < uvWidth; col++)
            {
                int uvIndex = row * uvRowStride + col * uvPixelStride;

                // NV21 is V first, then U (VUVUVU...)
                nv21[nv21Index++] = (byte)vBuffer.Get(uvIndex);
                nv21[nv21Index++] = (byte)uBuffer.Get(uvIndex);
            }
        }

        // **STEP 3: Create YuvImage with NV21 data**
        var yuvImage = new global::Android.Graphics.YuvImage(
            nv21,
            global::Android.Graphics.ImageFormatType.Nv21,
            width,
            height,
            null // No custom strides
        );

        // **STEP 4: Compress to JPEG**
        using var outputStream = new System.IO.MemoryStream();

        bool success = yuvImage.CompressToJpeg(
            new global::Android.Graphics.Rect(0, 0, width, height),
            95, // High quality
            outputStream
        );

        if (!success)
        {
            throw new InvalidOperationException("Failed to compress YUV to JPEG");
        }

        byte[] jpegBytes = outputStream.ToArray();

        // **STEP 5: Rotate to correct orientation**
        return RotateJpeg(jpegBytes, GetDeviceRotation());
    }

    private byte[] RotateJpeg(byte[] jpegBytes, int rotationDegrees)
    {
        if (rotationDegrees == 0)
        {
            return jpegBytes;
        }

        try
        {
            using var bitmap = BitmapFactory.DecodeByteArray(jpegBytes, 0, jpegBytes.Length);
            if (bitmap == null)
            {
                _logger.Warning("Could not decode JPEG for rotation");
                return jpegBytes;
            }

            using var matrix = new global::Android.Graphics.Matrix();
            matrix.PostRotate(rotationDegrees);

            using var rotatedBitmap = Bitmap.CreateBitmap(
                bitmap,
                0, 0,
                bitmap.Width, bitmap.Height,
                matrix,
                true
            );

            using var outputStream = new System.IO.MemoryStream();
            rotatedBitmap.Compress(Bitmap.CompressFormat.Jpeg!, 95, outputStream);

            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error rotating JPEG");
            return jpegBytes;
        }
    }

    private int GetDeviceRotation()
    {
        try
        {
            var windowManager = _ctx.GetSystemService(Context.WindowService) as IWindowManager;
            var rotation = windowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;

            // Convert display rotation to degrees
            return rotation switch
            {
                SurfaceOrientation.Rotation0 => 90,    // Portrait (natural)
                SurfaceOrientation.Rotation90 => 0,    // Landscape
                SurfaceOrientation.Rotation180 => 270, // Portrait (upside down)
                SurfaceOrientation.Rotation270 => 180, // Landscape (upside down)
                _ => 90
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not determine rotation, defaulting to 90°");
            return 90;
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
        catch (Exception ex)
        {
            _logger.Error(ex, "Error getting display rotation.");
            return SurfaceOrientation.Rotation0;
        }
    }

    // Background rendering
    private void InitializeGLResources()
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
        catch (Java.Lang.NoSuchMethodError ex)
        {
            _logger.Error(ex, "Error transforming display UV coordinates.");
        }
    }

    private async void OnImageAvailable(ImageReader? reader)
    {
        Image? image = null;
        bool captureSuccess = false;
        bool shouldReleaseSemaphore = false; // Track if this is the actual capture

        try
        {
            image = reader?.AcquireLatestImage();
            if (image == null)
            {
                _logger.Warning("OnImageAvailable: image is null");
                return;
            }

            if (!_readyToCapture)
            {
                // This is an AF-phase frame — discard it
                _logger.Information("OnImageAvailable: discarding AF frame");
                return;
            }

            // This is the actual capture frame
            shouldReleaseSemaphore = true;
            _logger.Information("OnImageAvailable: Processing capture!");
            _readyToCapture = false;

            var buffer = image.GetPlanes()?[0].Buffer;
            if (buffer != null)
            {
                byte[] jpegBytes = new byte[buffer.Remaining()];
                buffer.Get(jpegBytes);

                _logger.Information("Captured image: {Size} bytes", jpegBytes.Length);

                // Close image immediately after reading buffer
                try
                {
                    image?.Close();
                    image?.Dispose();
                    image = null;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error closing image");
                }

                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Invoke async event handler and WAIT for it to complete
                if (CaptureReady != null)
                {
                    var package = new CapturePackage
                    {
                        JpegBytes = jpegBytes,
                        MetadataJson = "",
                        FileBaseName = $"cap_{timestamp}"
                    };

                    try
                    {
                        // Wait for all handlers to complete
                        await Task.WhenAll(
                            CaptureReady.GetInvocationList()
                                .Cast<Func<CapturePackage, Task>>()
                                .Select(handler => handler(package))
                        );

                        _logger.Information("Image save completed successfully");
                        captureSuccess = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error in CaptureReady handler");
                    }
                }
            }

            if (_poses.Count > 100)
            {
                _poses.RemoveAt(0);
                _yaws.RemoveAt(0);
            }

            _poses.Add(_currentPose!);
            _yaws.Add(_currentYaw);
            _captures++;

            LogMemoryUsage();
            LogBatteryAndThermalInfo();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing captured image.");
            shouldReleaseSemaphore = true; // Release on error too
        }
        // In OnImageAvailable method, after line 2352, in the finally block:
        finally
        {
            // Close image if not already closed
            if (image != null)
            {
                try
                {
                    image.Close();
                    image.Dispose();
                }
                catch { }
            }

            // Only update _capturing and release semaphore if this was the actual capture
            if (shouldReleaseSemaphore)
            {
                _capturing = false;

                // **ADD THIS: Cleanup and resume ARCore**
                try
                {
                    CleanupCameraResources();

                    // Small delay to ensure camera is released
                    await Task.Delay(100);

                    // Resume ARCore session
                    if (_session != null)
                    {
                        _session.Resume();
                        _logger.Information("ARCore session resumed after capture completion");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error resuming ARCore after capture");
                }

                // Release semaphore
                try
                {
                    _captureSemaphore.Release();
                    _logger.Information("Capture completed. Success: {Success}, Semaphore released", captureSuccess);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error releasing semaphore");
                }
            }
        }
    }

    private bool _requestCapture = false;

    // Modify CaptureHighResPhoto to just set a flag:
    // Add this field at the top of the class:
    //private bool _requestCapture = false;
    private object _captureLock = new object();

    public Task CaptureHighResPhoto()
    {
        if (!_cameraTracking)
        {
            _logger.Information("Camera tracking not ready");
            return Task.CompletedTask;
        }

        if (_currentPose == null)
        {
            _logger.Information("AR tracking not ready");
            return Task.CompletedTask;
        }

        lock (_captureLock)
        {
            if (_capturing)
            {
                _logger.Information("Already capturing, ignoring request");
                return Task.CompletedTask;
            }

            // Set flag to capture on next GL frame
            _requestCapture = true;
        }

        _logger.Information("Capture requested");
        return Task.CompletedTask;
    }

    

    //private void CleanupCameraResources()
    //{
    //    try
    //    {
    //        // Close capture session
    //        if (_captureSession != null)
    //        {
    //            try
    //            {
    //                _captureSession.StopRepeating(); // Cancel pending requests
    //                _captureSession.AbortCaptures(); // Abort in-flight captures

    //                // **ADD: Small delay for MIUI to process abort**
    //                Thread.Sleep(100);
    //            }
    //            catch (Exception ex)
    //            {
    //                _logger.Warning(ex, "Error stopping capture session.");
    //            }
    //            finally
    //            {
    //                _captureSession = null;
    //            }
    //        }

    //        // Close camera device
    //        if (_cameraDevice != null)
    //        {
    //            try
    //            {
    //                _cameraDevice.Close();

    //                // **ADD: Critical delay for MIUI camera cloud service**
    //                if (_isMediaTekDevice)
    //                {
    //                    Thread.Sleep(300); // MIUI needs time to update cloud camera controller
    //                    _logger.Information("Applied MIUI camera release delay");
    //                }
    //            }
    //            catch (Exception ex)
    //            {
    //                _logger.Warning(ex, "Error closing camera device.");
    //            }
    //            finally
    //            {
    //                _cameraDevice = null;
    //            }
    //        }

    //        // Close image reader
    //        if (_imageReader != null)
    //        {
    //            try
    //            {
    //                _imageReader.Close();
    //            }
    //            catch (Exception ex)
    //            {
    //                _logger.Warning(ex, "Error closing image reader.");
    //            }
    //            finally
    //            {
    //                _imageReader = null;
    //            }
    //        }

    //        // Resume ARCore session after Camera2 cleanup
    //        try
    //        {
    //            if (_session != null)
    //            {
    //                // **INCREASE this delay for MIUI**
    //                if (_isMediaTekDevice)
    //                {
    //                    Thread.Sleep(200); // Extra time for MIUI to fully release camera
    //                }

    //                _session.Resume();
    //                _logger.Information("ARCore session resumed after camera cleanup");
    //            }
    //        }
    //        catch (Exception ex)
    //        {
    //            _logger.Error(ex, "Error resuming ARCore session after cleanup");
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.Error(ex, "Error in CleanupCameraResources.");
    //    }
    //}

    private void LogMemoryUsage()
    {
        try
        {
            var runtime = Java.Lang.Runtime.GetRuntime();
            if (runtime != null)
            {
                var usedMemMB = (runtime.TotalMemory() - runtime.FreeMemory()) / 1048576;
                var maxMemMB = runtime.MaxMemory() / 1048576;
                var percentUsed = (float)usedMemMB / maxMemMB * 100;

                _logger.Information("Memory: {UsedMB}MB / {MaxMB}MB ({Percent:F1}%)",
                    usedMemMB, maxMemMB, percentUsed);
                if (percentUsed > 80)
                {
                    _logger.Warning("Memory usage critical: {Percent:F1}%", percentUsed);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error logging memory usage.");
        }
    }

    // Тrack thermal state
    private int _currentThermalStatus = 0;
    private bool _thermalThrottlingActive = false;
    private bool _thermalWarningShown = false;

    private void LogBatteryAndThermalInfo()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var powerManager = context.GetSystemService(Context.PowerService) as PowerManager;

            if (powerManager != null)
            {
                // Thermal status (Android 9.0+)
                if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.P)
                {
                    try
                    {
                        var thermalStatusMethod = powerManager.Class.GetMethod("getCurrentThermalStatus");
                        if (thermalStatusMethod != null)
                        {
                            var thermalStatusObj = thermalStatusMethod.Invoke(powerManager);
                            int thermalStatus = (int)thermalStatusObj;
                            _currentThermalStatus = thermalStatus;

                            string thermalLevel = thermalStatus switch
                            {
                                0 => "None",
                                1 => "Light",
                                2 => "Moderate",
                                3 => "Severe",
                                4 => "Critical",
                                5 => "Emergency",
                                6 => "Shutdown",
                                _ => "Unknown"
                            };

                            _logger.Information("Thermal Status: {Status}", thermalLevel);

                            // Apply throttling at Moderate or higher
                            if (thermalStatus >= 2)
                            {
                                _thermalThrottlingActive = true;
                                _logger.Warning("Device thermal throttling detected: {Status}. Applying protective measures.", thermalLevel);

                                // Notify user once per session when reaching Severe or above
                                if (thermalStatus >= 3 && !_thermalWarningShown)
                                {
                                    _thermalWarningShown = true;
                                    MainThread.BeginInvokeOnMainThread(async () =>
                                    {
                                        try
                                        {
                                            if (Application.Current?.MainPage != null)
                                            {
                                                await Application.Current.MainPage.DisplayAlertAsync(
                                                    "Device Temperature Warning",
                                                    $"Your device is getting warm ({thermalLevel}). Capture speed and quality have been reduced to prevent overheating.",
                                                    "OK");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.Warning(ex, "Failed to show thermal warning dialog");
                                        }
                                    });
                                }
                            }
                            else
                            {
                                _thermalThrottlingActive = false;
                                _thermalWarningShown = false; // Reset when thermal state improves
                            }
                        }
                    }
                    catch (Exception thermalEx)
                    {
                        _logger.Debug(thermalEx, "Thermal status not available on this device");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error logging battery and thermal info.");
        }
    }

    private void CloseCameraSession()
    {
        try
        {
            _cameraDevice?.Close();
            _cameraDevice = null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error closing camera device.");
        }
    }

    private void LogDeviceInfo()
    {
        try
        {
            var manufacturer = Build.Manufacturer;
            var model = Build.Model;
            var version = Build.VERSION.Release;
            var sdkInt = Build.VERSION.SdkInt;
            _logger.Information("Device Info: {Manufacturer} {Model}, Android {Version} (SDK {SdkInt})",
                manufacturer, model, version, sdkInt);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error logging device info.");
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
                        // Target 2MP
                        var targetSize = sizes.Where(s => s.Width * s.Height <= 2_000_000)
                            .OrderByDescending(s => s.Width * s.Height)
                            .FirstOrDefault();

                        if (targetSize != null)
                        {
                            _cameraWidth = targetSize.Width;
                            _cameraHeight = targetSize.Height;
                        }
                        else
                        {
                            // If no 2MP size, use smallest available
                            targetSize = sizes.OrderByDescending(s => s.Width * s.Height)
                                .FirstOrDefault();

                            if (targetSize != null)
                            {
                                _cameraWidth = targetSize.Width;
                                _cameraHeight = targetSize.Height;
                            }
                        }

                        _logger.Information("Camera resolution set to: {Width}x{Height} (~{MP:F1}MP)",
                            _cameraWidth, _cameraHeight, (_cameraWidth * _cameraHeight) / 1_000_000.0);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Conservative fallback
            _cameraWidth = 2560;
            _cameraHeight = 1920;
            _logger.Error(ex, "Could not detect camera capabilities, using conservative defaults: {Width}x{Height}",
                _cameraWidth, _cameraHeight);
        }
    }

    private int GetJpegOrientation()
    {
        try
        {
            var cameraManager = _ctx.GetSystemService(Context.CameraService) as CameraManager;
            var cameraId = cameraManager?.GetCameraIdList().First();
            if (cameraId == null) return 90;

            var characteristics = cameraManager!.GetCameraCharacteristics(cameraId);
            var sensorOrientation = (int)(characteristics.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer)!;

            var displayRotation = GetDisplayRotation();
            int deviceDegrees = displayRotation switch
            {
                SurfaceOrientation.Rotation0 => 0,
                SurfaceOrientation.Rotation90 => 90,
                SurfaceOrientation.Rotation180 => 180,
                SurfaceOrientation.Rotation270 => 270,
                _ => 0
            };

            // For back-facing camera
            return (sensorOrientation - deviceDegrees + 360) % 360;
        }
        catch
        {
            return 90; // fallback
        }
    }

    // CameraDevice.StateCallback implementation
    private class CameraStateCallback : CameraDevice.StateCallback
    {
        private readonly ArCoreService _service;
        public CameraStateCallback(ArCoreService service)
        {
            _service = service;
        }

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
                _service._logger.Error(ex, "Camera setup error.");
            }
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            _service._logger.Information("Camera disconnected.");

            camera.Close();
            _service._cameraDevice = null;
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            _service._logger.Error("Camera error: {Error}", error);

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

    // CameraCaptureSession.StateCallback implementation
    private class CaptureSessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly ArCoreService _service;
        public CaptureSessionCallback(ArCoreService service)
        {
            _service = service;
        }

        public override void OnConfigured(CameraCaptureSession session)
        {
            _service._captureSession = session;

            try
            {
                // Start a repeating request with AF trigger to lock focus
                var previewRequest = _service._cameraDevice!.CreateCaptureRequest(CameraTemplate.StillCapture);
                previewRequest.AddTarget(_service._imageReader!.Surface!);
                previewRequest.Set(CaptureRequest.ControlAfMode!, (int)ControlAFMode.Auto);
                previewRequest.Set(CaptureRequest.ControlAfTrigger!, (int)ControlAFTrigger.Start);
                previewRequest.Set(CaptureRequest.ControlAeMode!, (int)ControlAEMode.On);
                previewRequest.Set(CaptureRequest.ControlAwbMode!, (int)ControlAwbMode.Auto);
                previewRequest.Set(CaptureRequest.ControlMode!, (int)ControlMode.Auto);

                session.Capture(
                    previewRequest.Build(),
                    new WaitForAfCallback(_service, session),
                    _service._backgroundHandler
                );

                _service._logger.Information("Waiting for AF lock...");
            }
            catch (Exception ex)
            {
                _service._logger.Error(ex, "Camera session error.");
                _service._capturing = false;
                _service._captureSemaphore.Release();
            }
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            _service._logger.Error("Camera session configuration failed.");
            _service._capturing = false;
            _service._captureSemaphore.Release();
        }
    }

    // Waits for AF to lock, then fires the actual still capture
    private class WaitForAfCallback : CameraCaptureSession.CaptureCallback
    {
        private readonly ArCoreService _service;
        private readonly CameraCaptureSession _session;
        private int _attempts = 0;
        private const int MaxAttempts = 10;

        public WaitForAfCallback(ArCoreService service, CameraCaptureSession session)
        {
            _service = service;
            _session = session;
        }

        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        {
            base.OnCaptureCompleted(session, request, result);

            var afState = result.Get(CaptureResult.ControlAfState);
            _attempts++;

            bool afLocked = afState != null &&
                ((ControlAFState)(int)(Java.Lang.Integer)afState == ControlAFState.FocusedLocked ||
                 (ControlAFState)(int)(Java.Lang.Integer)afState == ControlAFState.NotFocusedLocked);

            if (afLocked || _attempts >= MaxAttempts)
            {
                if (_attempts < MaxAttempts)
                    _service._logger.Information("AF locked. Capturing...");
                else
                    _service._logger.Warning("AF timeout. Capturing anyway...");

                FireStillCapture();
            }
            else
            {
                try
                {
                    var triggerRequest = _service._cameraDevice!.CreateCaptureRequest(CameraTemplate.StillCapture);
                    triggerRequest.AddTarget(_service._imageReader!.Surface!);
                    triggerRequest.Set(CaptureRequest.ControlAfMode!, (int)ControlAFMode.Auto);
                    triggerRequest.Set(CaptureRequest.ControlAfTrigger!, (int)ControlAFTrigger.Idle);
                    triggerRequest.Set(CaptureRequest.ControlAeMode!, (int)ControlAEMode.On);
                    triggerRequest.Set(CaptureRequest.ControlMode!, (int)ControlMode.Auto);
                    _session.Capture(triggerRequest.Build(), this, _service._backgroundHandler);
                }
                catch (Exception ex)
                {
                    _service._logger.Error(ex, "AF retry error.");
                    FireStillCapture();
                }
            }
        }

        private void FireStillCapture()
        {
            try
            {
                var stillRequest = _service._cameraDevice!.CreateCaptureRequest(CameraTemplate.StillCapture);
                stillRequest.AddTarget(_service._imageReader!.Surface!);
                stillRequest.Set(CaptureRequest.ControlAfMode!, (int)ControlAFMode.Auto);
                stillRequest.Set(CaptureRequest.ControlAeMode!, (int)ControlAEMode.On);
                stillRequest.Set(CaptureRequest.ControlAwbMode!, (int)ControlAwbMode.Auto);
                stillRequest.Set(CaptureRequest.ControlMode!, (int)ControlMode.Auto);
                stillRequest.Set(CaptureRequest.ControlCaptureIntent!, (int)ControlCaptureIntent.StillCapture);
                stillRequest.Set(CaptureRequest.JpegQuality!, (sbyte)85);
                stillRequest.Set(CaptureRequest.JpegOrientation!, _service.GetJpegOrientation());
                // stillRequest.Set(CaptureRequest.NoiseReductionMode!, (int)NoiseReductionMode.HighQuality);
                // stillRequest.Set(CaptureRequest.EdgeMode!, (int)EdgeMode.HighQuality);
                // stillRequest.Set(CaptureRequest.ColorCorrectionMode!, (int)ColorCorrectionMode.HighQuality);
                stillRequest.Set(CaptureRequest.NoiseReductionMode!, (int)NoiseReductionMode.Fast);
                stillRequest.Set(CaptureRequest.EdgeMode!, (int)EdgeMode.Fast);
                stillRequest.Set(CaptureRequest.ColorCorrectionMode!, (int)ColorCorrectionMode.Fast);
                stillRequest.Set(CaptureRequest.ControlVideoStabilizationMode!, (int)ControlVideoStabilizationMode.Off);

                _service._readyToCapture = true;

                _session.Capture(
                    stillRequest.Build(),
                    new CameraCaptureCallbackImpl(() =>
                    {
                        _service._logger.Information("High-quality capture completed.");
                    }),
                    _service._backgroundHandler
                );
            }
            catch (Exception ex)
            {
                _service._logger.Error($"Still capture error: {ex.Message}");
                _service._capturing = false;
                _service._captureSemaphore.Release();
            }
        }
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