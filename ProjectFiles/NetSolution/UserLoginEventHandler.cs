#region Using directives
using System;
using System.Collections.Generic;
using FTOptix.NetLogic;
using OpcUa = UAManagedCore.OpcUa;
using UAManagedCore;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.CoreBase;
using FTOptix.UI;
using FTOptix.AuditSigning;
using FTOptix.EventLogger;
using FTOptix.Store;
using FTOptix.Alarm;
using FTOptix.OPCUAServer;
using FTOptix.SQLiteStore;
using FTOptix.System;
using FTOptix.SerialPort;
using FTOptix.ODBCStore;
using FTOptix.DataLogger;
#endregion

/// <summary>
/// Observes user login events and copies user settings to the session.
/// Place this NetLogic under the NetLogic folder in FactoryTalk Optix Studio.
/// </summary>
public class UserLoginEventHandler : BaseNetLogic
{
    private IEventRegistration eventRegistration;

    public override void Start()
    {
        Log.Info("UserLoginEventHandler", "Starting user login event observer");
        
        var serverObject = LogicObject.Context.GetObject(OpcUa.Objects.Server);
        var eventHandler = new LoginEventObserver();
        eventRegistration = serverObject.RegisterUAEventObserver(eventHandler, FTOptix.Core.ObjectTypes.UserSessionEvent);
    }

    public override void Stop()
    {
        Log.Info("UserLoginEventHandler", "Stopping user login event observer");
        eventRegistration?.Dispose();
    }
}

public class LoginEventObserver : IUAEventObserver
{
    public LoginEventObserver()
    {
        Log.Info("LoginEventObserver", "Login event observer created");
    }

    public void OnEvent(IUAObject eventNotifier, IUAObjectType eventType, IReadOnlyList<object> eventData, ulong senderId)
    {
        var eventArguments = eventType.EventArguments;
        var sourceName = (string)eventArguments.GetFieldValue(eventData, "SourceName");

        // Only process Login events
        if (sourceName == "Login")
        {
            try
            {
                // Get the user that logged in
                var userNodeId = (NodeId)eventArguments.GetFieldValue(eventData, "UserId");
                var user = InformationModel.Get<User>(userNodeId);
                
                if (user == null)
                {
                    Log.Warning("LoginEventObserver", "Could not find user from login event");
                    return;
                }

                Log.Info("LoginEventObserver", $"User '{user.BrowseName}' logged in - applying user settings to session");

                // Get the SessionId from the event
                var sessionId = (NodeId)eventArguments.GetFieldValue(eventData, "SessionId");
                
                // Get the session object
                var session = InformationModel.Get(sessionId);
                if (session == null)
                {
                    Log.Warning("LoginEventObserver", "Could not get session object");
                    return;
                }

                // Copy user settings to session
                // Example: Copy Cfg_TagVis from User/UserSettings to Session
                CopyUserSettingToSession(user, session, "UserSettings/Cfg_TagVis", "Cfg_TagVis");
                
                // Add additional settings to copy here as needed:
                // CopyUserSettingToSession(user, session, "UserSettings/AnotherSetting", "AnotherSessionVar");
            }
            catch (Exception ex)
            {
                Log.Error("LoginEventObserver", $"Error handling login event: {ex.Message}");
            }
        }
        else if (sourceName == "Logout")
        {
            // Optional: Handle logout events if needed
            Log.Info("LoginEventObserver", "User logged out");
        }
    }

    /// <summary>
    /// Copies a user setting to the session variable
    /// </summary>
    /// <param name="user">The logged-in user</param>
    /// <param name="session">The session object</param>
    /// <param name="userSettingPath">Relative path to the user setting (e.g., "UserSettings/Cfg_TagVis")</param>
    /// <param name="sessionVariableName">Name of the session variable to set</param>
    private void CopyUserSettingToSession(IUANode user, IUANode session, string userSettingPath, string sessionVariableName)
    {
        var userSetting = user.GetVariable(userSettingPath);
        if (userSetting == null)
        {
            Log.Debug("LoginEventObserver", $"User '{user.BrowseName}' does not have setting '{userSettingPath}'");
            return;
        }

        var sessionVariable = session.GetVariable(sessionVariableName);
        if (sessionVariable == null)
        {
            Log.Warning("LoginEventObserver", $"Session does not have variable '{sessionVariableName}'");
            return;
        }

        sessionVariable.Value = userSetting.Value;
        Log.Info("LoginEventObserver", $"Copied {userSettingPath}={userSetting.Value} to Session/{sessionVariableName}");
    }
}
