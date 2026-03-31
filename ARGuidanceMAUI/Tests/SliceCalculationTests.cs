using System;
using ARGuidanceMAUI.Models;

namespace ARGuidanceMAUI.Tests
{
    /// <summary>
    /// Simple validation tests for the slice calculation logic
    /// </summary>
    public static class SliceCalculationTests
    {
        public static void RunAllTests()
        {
            Console.WriteLine("=== Slice Calculation Tests ===");
            
            TestCircleModeSliceCalculation();
            TestPlaneModeSliceCalculation();
            TestCaptureSettingsInitialization();
            
            Console.WriteLine("All tests completed successfully!");
        }
        
        public static void TestCircleModeSliceCalculation()
        {
            Console.WriteLine("\n--- Circle Mode Tests ---");
            
            var settings = new CaptureSettings
            {
                MovementType = MovementType.Circle,
                CapturesCount = 12
            };
            settings.InitializeSlices();
            
            // Test slice angle calculation
            var expectedAnglePerSlice = (float)(2 * Math.PI / 12); // 30 degrees in radians
            
            for (int i = 0; i < 12; i++)
            {
                var angle = settings.GetSliceAngleRadians(i);
                var expectedAngle = i * expectedAnglePerSlice;
                
                if (Math.Abs(angle - expectedAngle) > 0.001f)
                {
                    throw new Exception($"Circle mode slice {i}: expected {expectedAngle:F3}, got {angle:F3}");
                }
            }
            
            Console.WriteLine($"✓ Circle mode: 12 slices = {expectedAnglePerSlice * 180 / Math.PI:F1}° per slice");
            
            // Test with different capture count
            settings.CapturesCount = 8;
            var angle45 = settings.GetSliceAngleRadians(1); // Should be 45 degrees
            var expected45 = (float)(Math.PI / 4);
            
            if (Math.Abs(angle45 - expected45) > 0.001f)
            {
                throw new Exception($"8-slice mode: expected 45°, got {angle45 * 180 / Math.PI:F1}°");
            }
            
            Console.WriteLine($"✓ Circle mode: 8 slices = {expected45 * 180 / Math.PI:F1}° per slice");
        }
        
        public static void TestPlaneModeSliceCalculation()
        {
            Console.WriteLine("\n--- Plane Mode Tests ---");
            
            var settings = new CaptureSettings
            {
                MovementType = MovementType.Plane,
                CapturesCount = 10
            };
            settings.InitializeSlices();
            
            // Test slice position calculation
            for (int i = 0; i < 10; i++)
            {
                var position = settings.GetSlicePositionMeters(i);
                var expectedPosition = i * 0.1f;
                
                if (Math.Abs(position - expectedPosition) > 0.001f)
                {
                    throw new Exception($"Plane mode slice {i}: expected {expectedPosition:F1}m, got {position:F1}m");
                }
            }
            
            Console.WriteLine("✓ Plane mode: 0.1m increments working correctly");
            Console.WriteLine($"✓ Plane mode: 10 slices cover {10 * 0.1f:F1}m total distance");
        }
        
        public static void TestCaptureSettingsInitialization()
        {
            Console.WriteLine("\n--- Capture Settings Tests ---");
            
            var settings = new CaptureSettings
            {
                MovementType = MovementType.Circle,
                CapturesCount = 12
            };
            settings.InitializeSlices();
            
            // Test initial state
            if (settings.CapturedSlices.Length != 12)
            {
                throw new Exception($"Expected 12 slices, got {settings.CapturedSlices.Length}");
            }
            
            if (settings.GetCompletedCapturesCount() != 0)
            {
                throw new Exception($"Expected 0 completed captures initially, got {settings.GetCompletedCapturesCount()}");
            }
            
            // Test marking slices as captured
            settings.CapturedSlices[0] = true;
            settings.CapturedSlices[5] = true;
            
            if (settings.GetCompletedCapturesCount() != 2)
            {
                throw new Exception($"Expected 2 completed captures, got {settings.GetCompletedCapturesCount()}");
            }
            
            Console.WriteLine("✓ Capture settings initialization working correctly");
            Console.WriteLine("✓ Slice capture tracking working correctly");
        }
    }
}

// Example usage (would be called from a test runner or main method)
// SliceCalculationTests.RunAllTests();