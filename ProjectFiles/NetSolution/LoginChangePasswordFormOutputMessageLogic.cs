#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.UI;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.Retentivity;
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

public class LoginChangePasswordFormOutputMessageLogic : BaseNetLogic
{
    public override void Start()
    {
        HideMessageLabel();
        changePasswordResultCodeVariable = Owner.GetVariable("ChangePasswordResultCode");

        task = new DelayedTask(() =>
        {
            HideMessageLabel();
            taskStarted = false;
        }, 10000, LogicObject);
    }

    public override void Stop()
    {
        task?.Dispose();
    }

    [ExportMethod]
    public void SetOutputMessage(int resultCode)
    {
        if (changePasswordResultCodeVariable == null)
        {
            Log.Error("ChangePasswordFormOutputMessageLogic", "Unable to find ChangePasswordResultCode variable in ChangePasswordFormOutputMessage Label");
            return;
        }

        changePasswordResultCodeVariable.Value = resultCode;
        ShowMessageLabel();

        if (taskStarted)
        {
            task?.Cancel();
            taskStarted = false;
        }

        task.Start();
        taskStarted = true;
    }

    private void ShowMessageLabel()
    {
        var messageLabel = (Label)Owner;
        messageLabel.Visible = true;
    }

    private void HideMessageLabel()
    {
        var messageLabel = (Label)Owner;
        messageLabel.Visible = false;
    }

    private DelayedTask task;
    private bool taskStarted = false;
    private IUAVariable changePasswordResultCodeVariable;
}
