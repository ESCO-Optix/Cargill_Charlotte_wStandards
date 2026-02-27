#region Using directives
using System;
using System.Collections.Generic;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.SQLiteStore;
using FTOptix.Report;
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.EventLogger;
using FTOptix.CommunicationDriver;
using FTOptix.Alarm;
using FTOptix.SerialPort;
using FTOptix.Core;
using FTOptix.ODBCStore;
using FTOptix.DataLogger;
#endregion

/// <summary>
/// Keyboard input handler utility for FactoryTalk Optix.
/// 
/// HOW TO USE:
/// In FactoryTalk Optix, keyboard events are handled via EventHandlers configured in Studio:
/// 
/// 1. Place this NetLogic in the NetLogic folder
/// 2. On your TextBox or other UI element in Studio:
///    - Right-click → Add → EventHandler
///    - Set ListenEventType to the desired keyboard event (from dropdown)
///    - Set ObjectPointer to this NetLogic
///    - Set Method to one of the exported methods below (e.g., "OnEnterPressed")
///    - Add InputArguments as needed
/// 
/// For TextBox "Enter to confirm" behavior:
///    - ListenEventType: Select keyboard/key event type
///    - Method: OnEnterPressed
/// 
/// Note: Keyboard event types available depend on your FTOptix version.
/// Check the ListenEventType dropdown in Studio for available options.
/// </summary>
public class KeyInputEventHandler : BaseNetLogic
{
    public override void Start()
    {
        Log.Info("KeyInputEventHandler", "Keyboard input handler ready");
    }

    public override void Stop()
    {
        Log.Info("KeyInputEventHandler", "Keyboard input handler stopped");
    }

    /// <summary>
    /// Call this method when Enter key is pressed on a TextBox to confirm input.
    /// Configure EventHandler on TextBox:
    ///   - ObjectPointer: This NetLogic
    ///   - Method: OnEnterPressed
    /// </summary>
    [ExportMethod]
    public void OnEnterPressed()
    {
        Log.Info("KeyInputEventHandler", "Enter key pressed - confirming input");
        
        // Add your enter key handling logic here
        // Examples:
        // - Move focus to next field
        // - Validate and submit the value
        // - Close a dialog
    }

    /// <summary>
    /// Generic key handler with key code parameter.
    /// Configure EventHandler with InputArguments:
    ///   - key (Int32): The key code
    /// </summary>
    [ExportMethod]
    public void OnKeyPressed(int key)
    {
        Log.Info("KeyInputEventHandler", $"Key pressed: {key}");
        
        switch (key)
        {
            case 13: // Enter
            case 108: // NumPad Enter
                HandleEnterKey();
                break;
            case 27: // Escape
                HandleEscapeKey();
                break;
            case 9: // Tab
                HandleTabKey();
                break;
            default:
                // Handle function keys F1=112 through F12=123
                if (key >= 112 && key <= 123)
                {
                    HandleFunctionKey(key - 112 + 1);
                }
                else
                {
                    Log.Debug("KeyInputEventHandler", $"Unhandled key: {key}");
                }
                break;
        }
    }

    /// <summary>
    /// Key handler with modifier keys.
    /// Configure EventHandler with InputArguments:
    ///   - key (Int32), ctrlPressed (Boolean), shiftPressed (Boolean), altPressed (Boolean)
    /// </summary>
    [ExportMethod]
    public void OnKeyWithModifiers(int key, bool ctrlPressed, bool shiftPressed, bool altPressed)
    {
        Log.Info("KeyInputEventHandler", $"Key: {key}, Ctrl={ctrlPressed}, Shift={shiftPressed}, Alt={altPressed}");

        if (ctrlPressed)
        {
            HandleCtrlShortcut(key, shiftPressed);
            return;
        }

        OnKeyPressed(key);
    }

    /// <summary>
    /// Handle Escape key - call from EventHandler
    /// </summary>
    [ExportMethod]
    public void OnEscapePressed()
    {
        Log.Info("KeyInputEventHandler", "Escape pressed");
        HandleEscapeKey();
    }

    /// <summary>
    /// Handle a function key (F1-F12)
    /// </summary>
    [ExportMethod]
    public void OnFunctionKey(int fKeyNumber)
    {
        Log.Info("KeyInputEventHandler", $"F{fKeyNumber} pressed");
        HandleFunctionKey(fKeyNumber);
    }

    #region Key Handlers - Customize these for your application

    private void HandleEnterKey()
    {
        Log.Info("KeyInputEventHandler", "Enter key handled");
        // Add your Enter key logic:
        // - Validate input
        // - Submit form
        // - Move to next field
        // - Close dialog
    }

    private void HandleEscapeKey()
    {
        Log.Info("KeyInputEventHandler", "Escape key handled");
        // Add your Escape key logic:
        // - Cancel current operation
        // - Close popup/dialog
        // - Clear input field
    }

    private void HandleTabKey()
    {
        Log.Debug("KeyInputEventHandler", "Tab key handled");
        // Tab navigation is usually handled natively by FTOptix
    }

    private void HandleCtrlShortcut(int key, bool shiftPressed)
    {
        // Common Ctrl+key shortcuts
        // Key codes: A=65, C=67, S=83, V=86, X=88, Y=89, Z=90
        switch (key)
        {
            case 83: // Ctrl+S - Save
                Log.Info("KeyInputEventHandler", "Ctrl+S - Save");
                // Add save logic
                break;

            case 90: // Ctrl+Z - Undo (Ctrl+Shift+Z often = Redo)
                if (shiftPressed)
                    Log.Info("KeyInputEventHandler", "Ctrl+Shift+Z - Redo");
                else
                    Log.Info("KeyInputEventHandler", "Ctrl+Z - Undo");
                break;

            case 89: // Ctrl+Y - Redo
                Log.Info("KeyInputEventHandler", "Ctrl+Y - Redo");
                break;

            case 65: // Ctrl+A - Select All
                Log.Info("KeyInputEventHandler", "Ctrl+A - Select All");
                break;

            case 67: // Ctrl+C - Copy (usually native)
                Log.Debug("KeyInputEventHandler", "Ctrl+C - Copy (native)");
                break;

            case 86: // Ctrl+V - Paste (usually native)
                Log.Debug("KeyInputEventHandler", "Ctrl+V - Paste (native)");
                break;

            case 88: // Ctrl+X - Cut (usually native)
                Log.Debug("KeyInputEventHandler", "Ctrl+X - Cut (native)");
                break;

            default:
                Log.Debug("KeyInputEventHandler", $"Ctrl+{(char)key} - No handler");
                break;
        }
    }

    private void HandleFunctionKey(int fKeyNumber)
    {
        switch (fKeyNumber)
        {
            case 1: // F1 - Help
                Log.Info("KeyInputEventHandler", "F1 - Help");
                // Open help screen or documentation
                break;

            case 5: // F5 - Refresh
                Log.Info("KeyInputEventHandler", "F5 - Refresh");
                // Refresh current view/data
                break;

            case 11: // F11 - Full screen (common convention)
                Log.Info("KeyInputEventHandler", "F11 - Toggle Full Screen");
                break;

            default:
                Log.Debug("KeyInputEventHandler", $"F{fKeyNumber} - No handler defined");
                break;
        }
    }

    #endregion

    #region Key Code Reference
    // Common Windows Virtual Key Codes:
    // Enter = 13, NumPad Enter = 108
    // Escape = 27
    // Tab = 9
    // Backspace = 8
    // Delete = 46
    // Space = 32
    // Arrow keys: Left=37, Up=38, Right=39, Down=40
    // Home = 36, End = 35
    // Page Up = 33, Page Down = 34
    // F1-F12 = 112-123
    // Letters A-Z = 65-90
    // Numbers 0-9 = 48-57
    // NumPad 0-9 = 96-105
    #endregion
}
