#region Using directives
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Report;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.EventLogger;
using FTOptix.CommunicationDriver;
using FTOptix.Store;
using FTOptix.Alarm;
using FTOptix.SerialPort;
using FTOptix.Core;
using FTOptix.SQLiteStore;
using FTOptix.ODBCStore;
using FTOptix.AuditSigning;
#endregion

/// <summary>
/// MouseTracking NetLogic - Tracks mouse cursor position using Windows API.
/// 
/// OVERVIEW:
/// Uses P/Invoke to call Windows user32.dll GetCursorPos function to track
/// the mouse cursor position in screen coordinates. Runs in a separate thread
/// via PeriodicTask for minimal performance impact.
/// 
/// Also provides hit testing capabilities to detect when the mouse is over
/// specific UI elements using Windows API ClientToScreen for coordinate conversion.
/// 
/// REQUIRED VARIABLES (create as children of the NetLogic object):
/// - MouseX (Int32): Current X position of the mouse cursor in screen coordinates
/// - MouseY (Int32): Current Y position of the mouse cursor in screen coordinates
/// - PollingInterval (Int32): Interval in milliseconds between position updates (default: 50ms)
/// - Enabled (Boolean): Enable/disable tracking at runtime
/// 
/// OPTIONAL VARIABLES FOR HIT TESTING:
/// - HitTargetNodeId (NodeId): NodeId of the UI element currently under the mouse (empty if none)
/// - HitTargetPath (String): BrowseName of the UI element currently under the mouse
/// - IsOverTarget (Boolean): True if mouse is over any registered target
/// - DesignWidth (Int32): Your Optix project's design width (e.g., 1920) - RECOMMENDED for accuracy
/// - DesignHeight (Int32): Your Optix project's design height (e.g., 1080) - RECOMMENDED for accuracy
/// 
/// HIT TESTING:
/// Call RegisterHitTarget() to add UI elements for hit testing. The script will
/// check if the mouse is within the bounds of registered elements and update
/// the hit testing variables accordingly.
/// 
/// PERFORMANCE NOTES:
/// - Uses PeriodicTask which runs in a separate thread pool thread
/// - Default 50ms polling (20 updates/sec) provides smooth tracking with minimal CPU
/// - GetCursorPos is a lightweight Windows API call with negligible overhead
/// - Position values only update when they change to minimize variable write operations
/// 
/// USAGE:
/// 1. Add this NetLogic to a Panel or Screen
/// 2. Create the required variables as children of the NetLogic
/// 3. Bind UI elements to MouseX/MouseY variables for real-time position display
/// 4. Optionally call RegisterHitTarget() to enable hit testing on specific elements
/// </summary>
public class MouseTracking : BaseNetLogic
{
    #region Windows API Imports

    /// <summary>
    /// Structure to hold cursor position coordinates.
    /// Must match Windows POINT structure layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Structure to hold window rectangle coordinates.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Retrieves the position of the mouse cursor, in screen coordinates.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Retrieves a handle to the foreground window.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Retrieves the dimensions of the bounding rectangle of the specified window.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// Retrieves the client area dimensions of the specified window.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// Converts the client-area coordinates to screen coordinates.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>
    /// Retrieves system metrics such as screen width/height.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Retrieves the DPI for a specific window. Available on Windows 10 1607+.
    /// Returns 96 for 100% scaling, 144 for 150%, 192 for 200%, 216 for 225%, etc.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>
    /// Retrieves the system DPI. Fallback for older Windows versions.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// Gets device-specific information (used as fallback for DPI detection).
    /// </summary>
    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    /// <summary>
    /// Gets a device context for the entire screen.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    /// <summary>
    /// Releases a device context.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    #endregion

    #region Constants

    private const int DEFAULT_POLLING_INTERVAL_MS = 50;
    private const int MIN_POLLING_INTERVAL_MS = 10;
    private const int MAX_POLLING_INTERVAL_MS = 1000;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int LOGPIXELSX = 88;
    private const int LOGPIXELSY = 90;
    private const float DEFAULT_DPI = 96.0f;

    #endregion

    #region Private Fields

    private PeriodicTask mouseTrackingTask;
    private IUAVariable mouseXVariable;
    private IUAVariable mouseYVariable;
    private IUAVariable pollingIntervalVariable;
    private IUAVariable enabledVariable;
    
    // Hit testing variables (optional)
    private IUAVariable hitTargetNodeIdVariable;
    private IUAVariable hitTargetPathVariable;
    private IUAVariable isOverTargetVariable;
    private IUAVariable screenWidthVariable;
    private IUAVariable screenHeightVariable;
    
    // Design resolution variables (optional but recommended for accurate hit testing)
    private IUAVariable designWidthVariable;
    private IUAVariable designHeightVariable;

    private int lastMouseX = int.MinValue;
    private int lastMouseY = int.MinValue;
    private readonly object lockObject = new object();
    private int defaultPollingInterval = DEFAULT_POLLING_INTERVAL_MS;
    private bool defaultEnabled = true;

    /// <summary>
    /// Dictionary of registered hit test targets: NodeId -> UI element
    /// </summary>
    private readonly Dictionary<NodeId, Item> hitTestTargets = new Dictionary<NodeId, Item>();
    
    /// <summary>
    /// Lock for thread-safe access to hit test targets
    /// </summary>
    private readonly object hitTestLock = new object();

    /// <summary>
    /// Last hit target to avoid unnecessary updates
    /// </summary>
    private NodeId lastHitTargetNodeId = null;

    /// <summary>
    /// Reference to the presentation engine window for coordinate conversion
    /// </summary>
    private PresentationEngine presentationEngine;

    /// <summary>
    /// Tracks whether mouse tracking task is running to avoid duplicate logs.
    /// </summary>
    private bool isTrackingRunning;

    /// <summary>
    /// Timestamp for throttled hit-test debug logging.
    /// </summary>
    private int lastHitTestDebugLogTick;

    /// <summary>
    /// Timestamp for throttled window/bounds debug logging.
    /// </summary>
    private int lastBoundsDebugLogTick;

    /// <summary>
    /// Cached scaling factors for client-to-logical coordinates.
    /// </summary>
    private float scaleX = 1.0f;
    private float scaleY = 1.0f;
    private bool scaleInitialized;

    /// <summary>
    /// Cached DPI scale factor (e.g., 2.25 for 225% scaling).
    /// </summary>
    private float dpiScaleFactor = 1.0f;

    #endregion

    #region Lifecycle Methods

    public override void Start()
    {
        try
        {
            if (!InitializeVariables())
            {
                Log.Error(GetType().Name, "Failed to initialize required variables. Mouse tracking will not start.");
                return;
            }

            // Try to get the presentation engine for coordinate conversion
            presentationEngine = Owner?.Get<PresentationEngine>("NativePresentation") ?? 
                                 Owner?.Owner?.Get<PresentationEngine>("NativePresentation");

            if (enabledVariable != null)
                enabledVariable.VariableChange += EnabledVariable_VariableChange;

            if (pollingIntervalVariable != null)
                pollingIntervalVariable.VariableChange += PollingIntervalVariable_VariableChange;

            bool isEnabled = enabledVariable != null ? (bool)enabledVariable.Value : defaultEnabled;
            if (isEnabled)
            {
                StartMouseTracking();
            }

            int currentInterval = pollingIntervalVariable != null ? (int)pollingIntervalVariable.Value : defaultPollingInterval;
            Log.Info(GetType().Name, $"Mouse tracking initialized. Enabled: {isEnabled}, Interval: {currentInterval}ms");
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, $"Error starting mouse tracking: {ex.Message}");
        }
    }

    public override void Stop()
    {
        try
        {
            if (enabledVariable != null)
                enabledVariable.VariableChange -= EnabledVariable_VariableChange;

            if (pollingIntervalVariable != null)
                pollingIntervalVariable.VariableChange -= PollingIntervalVariable_VariableChange;

            StopMouseTracking();
            
            lock (hitTestLock)
            {
                hitTestTargets.Clear();
            }

            Log.Info(GetType().Name, "Mouse tracking stopped and cleaned up.");
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, $"Error stopping mouse tracking: {ex.Message}");
        }
    }

    #endregion

    #region Variable Initialization

    private bool InitializeVariables()
    {
        bool allVariablesFound = true;

        mouseXVariable = LogicObject.GetVariable("MouseX");
        if (mouseXVariable == null)
        {
            Log.Error(GetType().Name, "MouseX variable not found. Please create an Int32 variable named 'MouseX' as a child of this NetLogic.");
            allVariablesFound = false;
        }

        mouseYVariable = LogicObject.GetVariable("MouseY");
        if (mouseYVariable == null)
        {
            Log.Error(GetType().Name, "MouseY variable not found. Please create an Int32 variable named 'MouseY' as a child of this NetLogic.");
            allVariablesFound = false;
        }

        pollingIntervalVariable = LogicObject.GetVariable("PollingInterval");
        if (pollingIntervalVariable == null)
        {
            Log.Warning(GetType().Name, $"PollingInterval variable not found. Using default value {DEFAULT_POLLING_INTERVAL_MS}ms.");
            defaultPollingInterval = DEFAULT_POLLING_INTERVAL_MS;
        }

        enabledVariable = LogicObject.GetVariable("Enabled");
        if (enabledVariable == null)
        {
            Log.Warning(GetType().Name, "Enabled variable not found. Tracking will be enabled by default.");
            defaultEnabled = true;
        }

        // Optional hit testing variables
        hitTargetNodeIdVariable = LogicObject.GetVariable("HitTargetNodeId");
        hitTargetPathVariable = LogicObject.GetVariable("HitTargetPath");
        isOverTargetVariable = LogicObject.GetVariable("IsOverTarget");

        // Optional screen dimension variables
        screenWidthVariable = LogicObject.GetVariable("ScreenWidth");
        screenHeightVariable = LogicObject.GetVariable("ScreenHeight");
        
        // Optional design resolution variables - HIGHLY RECOMMENDED for accurate hit testing
        // Set these to your Optix project's design resolution (e.g., 1920x1080)
        designWidthVariable = LogicObject.GetVariable("DesignWidth");
        designHeightVariable = LogicObject.GetVariable("DesignHeight");
        
        if (designWidthVariable == null || designHeightVariable == null)
        {
            Log.Warning(GetType().Name, "DesignWidth/DesignHeight variables not found. " +
                "For accurate hit testing, create Int32 variables matching your Optix design resolution (e.g., 1920x1080).");
        }

        return allVariablesFound;
    }

    #endregion

    #region Public Hit Testing Methods

    /// <summary>
    /// Registers a UI element for hit testing. When the mouse is over this element,
    /// the HitTargetNodeId and HitTargetPath variables will be updated.
    /// </summary>
    /// <param name="target">The UI Item to register for hit testing</param>
    [ExportMethod]
    public void RegisterHitTarget(NodeId targetNodeId)
    {
        try
        {
            var target = InformationModel.Get(targetNodeId) as Item;
            if (target == null)
            {
                Log.Warning(GetType().Name, $"Cannot register hit target: Node {targetNodeId} is not a UI Item.");
                return;
            }

            lock (hitTestLock)
            {
                if (!hitTestTargets.ContainsKey(targetNodeId))
                {
                    hitTestTargets[targetNodeId] = target;
                    if (TryGetItemScreenBounds(target, out float left, out float top, out float right, out float bottom))
                    {
                        Log.Info(GetType().Name, $"Registered hit target: {target.BrowseName} bounds L={left:0.##}, T={top:0.##}, R={right:0.##}, B={bottom:0.##}");
                    }
                    else
                    {
                        Log.Info(GetType().Name, $"Registered hit target: {target.BrowseName} bounds unavailable");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, $"Error registering hit target: {ex.Message}");
        }
    }

    /// <summary>
    /// Unregisters a UI element from hit testing.
    /// </summary>
    /// <param name="targetNodeId">The NodeId of the Item to unregister</param>
    [ExportMethod]
    public void UnregisterHitTarget(NodeId targetNodeId)
    {
        try
        {
            lock (hitTestLock)
            {
                if (hitTestTargets.Remove(targetNodeId))
                {
                    Log.Info(GetType().Name, $"Unregistered hit target: {targetNodeId}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, $"Error unregistering hit target: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears all registered hit test targets.
    /// </summary>
    [ExportMethod]
    public void ClearHitTargets()
    {
        lock (hitTestLock)
        {
            hitTestTargets.Clear();
            Log.Info(GetType().Name, "Cleared all hit targets.");
        }
    }

    /// <summary>
    /// Checks if the specified point is within the bounds of a UI element.
    /// Uses the element's position relative to its parent and dimensions.
    /// </summary>
    /// <param name="targetNodeId">NodeId of the UI element to check</param>
    /// <param name="screenX">X coordinate in screen space</param>
    /// <param name="screenY">Y coordinate in screen space</param>
    /// <returns>True if the point is within the element's bounds</returns>
    [ExportMethod]
    public bool IsPointOverElement(NodeId targetNodeId, int screenX, int screenY)
    {
        try
        {
            var target = InformationModel.Get(targetNodeId) as Item;
            if (target == null)
                return false;

            return IsPointOverItem(target, screenX, screenY);
        }
        catch (Exception ex)
        {
            Log.Warning(GetType().Name, $"Error checking point over element: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Event Handlers

    private void EnabledVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            if ((bool)e.NewValue)
            {
                StartMouseTracking();
            }
            else
            {
                StopMouseTracking();
            }
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, $"Error handling enabled change: {ex.Message}");
        }
    }

    private void PollingIntervalVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            bool isEnabled = enabledVariable != null ? (bool)enabledVariable.Value : defaultEnabled;
            if (isEnabled)
            {
                int newInterval = ValidatePollingInterval((int)e.NewValue);
                Log.Info(GetType().Name, $"Polling interval changed to {newInterval}ms. Restarting tracking.");
                RestartMouseTracking();
            }
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, $"Error handling polling interval change: {ex.Message}");
        }
    }

    #endregion

    #region Tracking Control Methods

    private void StartMouseTracking()
    {
        lock (lockObject)
        {
            if (mouseTrackingTask != null)
                return;

            int currentInterval = pollingIntervalVariable != null ? (int)pollingIntervalVariable.Value : defaultPollingInterval;
            int interval = ValidatePollingInterval(currentInterval);
            mouseTrackingTask = new PeriodicTask(UpdateMousePosition, interval, LogicObject);
            mouseTrackingTask.Start();

            if (!isTrackingRunning)
            {
                isTrackingRunning = true;
                Log.Info(GetType().Name, "Mouse tracking started.");
            }
        }
    }

    private void StopMouseTracking()
    {
        lock (lockObject)
        {
            if (mouseTrackingTask != null)
            {
                mouseTrackingTask.Dispose();
                mouseTrackingTask = null;
            }

            if (isTrackingRunning)
            {
                isTrackingRunning = false;
                Log.Info(GetType().Name, "Mouse tracking stopped.");
            }
        }
    }

    private void RestartMouseTracking()
    {
        lock (lockObject)
        {
            StopMouseTracking();
            StartMouseTracking();
        }
    }

    private int ValidatePollingInterval(int requestedInterval)
    {
        if (requestedInterval < MIN_POLLING_INTERVAL_MS)
        {
            Log.Warning(GetType().Name, $"Requested interval {requestedInterval}ms is below minimum. Using {MIN_POLLING_INTERVAL_MS}ms.");
            return MIN_POLLING_INTERVAL_MS;
        }
        if (requestedInterval > MAX_POLLING_INTERVAL_MS)
        {
            Log.Warning(GetType().Name, $"Requested interval {requestedInterval}ms exceeds maximum. Using {MAX_POLLING_INTERVAL_MS}ms.");
            return MAX_POLLING_INTERVAL_MS;
        }
        return requestedInterval;
    }

    #endregion

    #region Mouse Position Update

    private void UpdateMousePosition()
    {
        try
        {
            if (GetCursorPos(out POINT point))
            {
                // Update position variables only when changed
                if (point.X != lastMouseX)
                {
                    lastMouseX = point.X;
                    mouseXVariable.Value = point.X;
                }

                if (point.Y != lastMouseY)
                {
                    lastMouseY = point.Y;
                    mouseYVariable.Value = point.Y;
                }

                // Perform hit testing if we have targets and hit testing variables
                if (hitTestTargets.Count > 0 && (hitTargetNodeIdVariable != null || hitTargetPathVariable != null || isOverTargetVariable != null))
                {
                    PerformHitTest(point.X, point.Y);
                }
                else
                {
                    LogHitTestDebug(point.X, point.Y, "Hit testing skipped (no targets or output variables).", force: false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(GetType().Name, $"Error updating mouse position: {ex.Message}");
        }
    }

    #endregion

    #region Hit Testing

    /// <summary>
    /// Performs hit testing against all registered targets.
    /// </summary>
    private void PerformHitTest(int screenX, int screenY)
    {
        try
        {
            NodeId hitNodeId = null;
            string hitPath = null;

            bool hadTargets;
            lock (hitTestLock)
                hadTargets = hitTestTargets.Count > 0;

            if (!hadTargets)
            {
                LogHitTestDebug(screenX, screenY, "No registered targets.", force: false);
                return;
            }

            lock (hitTestLock)
            {
                foreach (var kvp in hitTestTargets)
                {
                    if (IsPointOverItem(kvp.Value, screenX, screenY))
                    {
                        hitNodeId = kvp.Key;
                        hitPath = kvp.Value.BrowseName;
                        break; // First hit wins (could be modified for z-order)
                    }
                }
            }

            if (hitNodeId == null)
                LogHitTestDebug(screenX, screenY, "No hit detected.", force: false);

            // Only update if hit target changed
            if (hitNodeId != lastHitTargetNodeId)
            {
                lastHitTargetNodeId = hitNodeId;

                if (hitNodeId != null)
                    Log.Info(GetType().Name, $"Hit target hovered: {hitPath ?? hitNodeId.ToString()}");

                if (hitTargetNodeIdVariable != null)
                    hitTargetNodeIdVariable.Value = hitNodeId ?? NodeId.Empty;

                if (hitTargetPathVariable != null)
                    hitTargetPathVariable.Value = hitPath ?? string.Empty;

                if (isOverTargetVariable != null)
                    isOverTargetVariable.Value = hitNodeId != null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(GetType().Name, $"Error performing hit test: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if a screen point is within a UI Item's bounds.
    /// Calculates the absolute screen position by walking up the parent hierarchy.
    /// </summary>
    private bool IsPointOverItem(Item item, int screenX, int screenY)
    {
        try
        {
            if (!TryGetItemScreenBounds(item, out float screenLeft, out float screenTop, out float screenRight, out float screenBottom))
            {
                LogHitTestDebug(screenX, screenY, $"Bounds unavailable for {item?.BrowseName}.", force: false);
                return false;
            }
            return screenX >= screenLeft && screenX <= screenRight &&
                   screenY >= screenTop && screenY <= screenBottom;
        }
        catch (Exception ex)
        {
            Log.Warning(GetType().Name, $"Error checking bounds for {item?.BrowseName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Calculates the screen bounds for a UI item.
    /// </summary>
    private bool TryGetItemScreenBounds(Item item, out float left, out float top, out float right, out float bottom)
    {
        left = top = right = bottom = 0;

        try
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
                return false;

            if (!GetWindowRect(hWnd, out RECT windowRect))
                return false;

            if (!EnsureScaleFactors(item, hWnd))
                return false;

            float absoluteX = 0;
            float absoluteY = 0;

            IUANode current = item;
            while (current != null)
            {
                if (current is Item uiItem)
                {
                    absoluteX += uiItem.LeftMargin;
                    absoluteY += uiItem.TopMargin;
                }
                current = current.Owner;
            }

            float scaledX = absoluteX * scaleX;
            float scaledY = absoluteY * scaleY;

            var clientPoint = new POINT
            {
                X = (int)Math.Round(scaledX),
                Y = (int)Math.Round(scaledY)
            };

            if (!ClientToScreen(hWnd, ref clientPoint))
                return false;

            left = clientPoint.X;
            top = clientPoint.Y;
            right = left + (item.Width * scaleX);
            bottom = top + (item.Height * scaleY);

            LogBoundsDebug(item, windowRect, left, top, right, bottom);

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(GetType().Name, $"Error calculating bounds for {item?.BrowseName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Throttled debug logging for window rect and computed bounds.
    /// </summary>
    private void LogBoundsDebug(Item item, RECT windowRect, float left, float top, float right, float bottom)
    {
        int now = Environment.TickCount;
        if (unchecked(now - lastBoundsDebugLogTick) > 1000)
        {
            lastBoundsDebugLogTick = now;
            Log.Info(GetType().Name,
                $"BoundsDebug: DPI={dpiScaleFactor * 100:0}% " +
                $"Window=({windowRect.Left},{windowRect.Top},{windowRect.Right},{windowRect.Bottom}) " +
                $"Item={item?.BrowseName} Bounds=({left:0.##},{top:0.##},{right:0.##},{bottom:0.##}) " +
                $"Size=({item?.Width:0.##},{item?.Height:0.##}) Scale=({scaleX:0.###},{scaleY:0.###})");
        }
    }

    /// <summary>
    /// Gets the DPI scale factor for the given window handle.
    /// Falls back to system DPI or device caps if GetDpiForWindow is unavailable.
    /// </summary>
    private float GetDpiScaleFactor(IntPtr hWnd)
    {
        try
        {
            // Try GetDpiForWindow first (Windows 10 1607+)
            uint dpi = GetDpiForWindow(hWnd);
            if (dpi > 0)
            {
                return dpi / DEFAULT_DPI;
            }
        }
        catch
        {
            // GetDpiForWindow may not be available on older Windows
        }

        try
        {
            // Fallback to GetDpiForSystem
            uint systemDpi = GetDpiForSystem();
            if (systemDpi > 0)
            {
                return systemDpi / DEFAULT_DPI;
            }
        }
        catch
        {
            // GetDpiForSystem may not be available
        }

        try
        {
            // Final fallback: use GetDeviceCaps
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                try
                {
                    int dpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                    if (dpiX > 0)
                    {
                        return dpiX / DEFAULT_DPI;
                    }
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }
        catch
        {
            // Ignore errors in fallback
        }

        return 1.0f; // Default to no scaling
    }

    /// <summary>
    /// Calculates and caches scaling factors based on window size vs. design resolution.
    /// Uses the actual window rectangle (which is in the same coordinate space as cursor position)
    /// and the configured design resolution (or auto-detected from UI hierarchy).
    /// </summary>
    private bool EnsureScaleFactors(Item item, IntPtr hWnd)
    {
        if (scaleInitialized)
            return true;

        try
        {
            // Get the window rectangle - this is in screen coordinates, same as GetCursorPos
            if (!GetWindowRect(hWnd, out RECT windowRect))
                return false;
            
            // Get client rect for reference/logging
            GetClientRect(hWnd, out RECT clientRect);
            
            // Get DPI for logging purposes
            dpiScaleFactor = GetDpiScaleFactor(hWnd);

            // Window dimensions in screen coordinates
            float windowWidth = windowRect.Right - windowRect.Left;
            float windowHeight = windowRect.Bottom - windowRect.Top;
            
            // Client dimensions (content area, excludes window borders/title bar)
            float clientWidth = clientRect.Right - clientRect.Left;
            float clientHeight = clientRect.Bottom - clientRect.Top;

            float screenWidth = GetSystemMetrics(SM_CXSCREEN);
            float screenHeight = GetSystemMetrics(SM_CYSCREEN);

            if (screenWidthVariable != null)
                screenWidthVariable.Value = (int)screenWidth;
            if (screenHeightVariable != null)
                screenHeightVariable.Value = (int)screenHeight;

            // Determine design resolution - prefer explicit configuration
            float designWidth = 0;
            float designHeight = 0;
            string designSource = "";
            
            if (designWidthVariable != null && designHeightVariable != null)
            {
                designWidth = (int)designWidthVariable.Value;
                designHeight = (int)designHeightVariable.Value;
                designSource = "configured";
            }
            
            // Fallback: try to find a Screen node in the hierarchy
            if (designWidth <= 0 || designHeight <= 0)
            {
                IUANode current = item;
                while (current != null)
                {
                    // Look for Screen type or large panel that's a reasonable size
                    if (current is Screen screen)
                    {
                        designWidth = screen.Width;
                        designHeight = screen.Height;
                        designSource = $"Screen:{screen.BrowseName}";
                        break;
                    }
                    current = current.Owner;
                }
            }
            
            // Fallback: find the topmost Item with reasonable dimensions (not a thin bar)
            if (designWidth <= 0 || designHeight <= 0)
            {
                Item topMostItem = null;
                IUANode current = item;
                while (current != null)
                {
                    if (current is Item uiItem)
                    {
                        // Only consider items with reasonable aspect ratios (not thin bars)
                        float aspectRatio = uiItem.Width / Math.Max(uiItem.Height, 1);
                        if (aspectRatio >= 0.5f && aspectRatio <= 3.0f && uiItem.Width >= 800 && uiItem.Height >= 400)
                        {
                            topMostItem = uiItem;
                        }
                    }
                    current = current.Owner;
                }
                
                if (topMostItem != null)
                {
                    designWidth = topMostItem.Width;
                    designHeight = topMostItem.Height;
                    designSource = $"auto:{topMostItem.BrowseName}";
                }
            }
            
            // Final fallback: use common design resolution
            if (designWidth <= 0 || designHeight <= 0)
            {
                designWidth = 1920;
                designHeight = 1080;
                designSource = "default";
                Log.Warning(GetType().Name, "Could not determine design resolution. Using default 1920x1080. " +
                    "For accuracy, create DesignWidth and DesignHeight variables.");
            }

            // Calculate scale factors: map Optix design units to screen pixels
            // The client area is where the Optix content is rendered
            scaleX = clientWidth / designWidth;
            scaleY = clientHeight / designHeight;
            scaleInitialized = true;

            Log.Info(GetType().Name, 
                $"ScaleDebug: DPI={dpiScaleFactor * 100:0}% " +
                $"Screen=({screenWidth:0.##},{screenHeight:0.##}) " +
                $"Window=({windowWidth:0.##},{windowHeight:0.##}) " +
                $"Client=({clientWidth:0.##},{clientHeight:0.##}) " +
                $"Design=({designWidth:0.##},{designHeight:0.##}) [{designSource}] " +
                $"Scale=({scaleX:0.###},{scaleY:0.###})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(GetType().Name, $"Error calculating scale factors: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Throttled debug logging to help troubleshoot hit testing.
    /// </summary>
    private void LogHitTestDebug(int screenX, int screenY, string message, bool force)
    {
        int now = Environment.TickCount;
        if (force || unchecked(now - lastHitTestDebugLogTick) > 1000)
        {
            lastHitTestDebugLogTick = now;
            Log.Info(GetType().Name, $"HitTestDebug: {message} Mouse=({screenX},{screenY}) Targets={hitTestTargets.Count}");
        }
    }

    #endregion
}
