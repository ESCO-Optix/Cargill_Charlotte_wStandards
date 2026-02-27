#region Using directives
using System;
using System.Collections.Generic;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.OPCUAClient;
using FTOptix.System;
using FTOptix.EdgeAppPlatform;
using FTOptix.Store;
using FTOptix.ODBCStore;
using FTOptix.Report;
using FTOptix.EventLogger;
using FTOptix.DataLogger;
using FTOptix.Alarm;
using FTOptix.SerialPort;
using FTOptix.SQLiteStore;
using FTOptix.AuditSigning;
#endregion

public class BackProvider : BaseNetLogic
{
    public override void Start()
    {
		oldPanelStack = new Stack<NodeId>();

		var panelLoader = Owner as PanelLoader;
		if (panelLoader == null)
			Log.Error("BackProvider", "Panel loader not found");
		panelLoader.PanelVariable.VariableChange += PanelVariable_VariableChange;
	}

	private void PanelVariable_VariableChange(object sender, VariableChangeEventArgs e)
	{
		var oldPanel = InformationModel.Get(e.OldValue);
		NodeId oldPanelNodeId = e.OldValue;
		oldPanelStack.Push(oldPanelNodeId);
	}

	public override void Stop()
    {
		var panelLoader = Owner as PanelLoader;
		if (panelLoader == null)
			Log.Error("BackProvider", "Panel loader not found");

		panelLoader.PanelVariable.VariableChange -= PanelVariable_VariableChange;
	}

	[ExportMethod]
	public void Back()
	{
		var panelLoader = Owner as PanelLoader;
		if (panelLoader == null)
			Log.Error("BackProvider", "Panel loader not found");

		if (oldPanelStack.Count == 0)
			return;

		var panelNodeId = oldPanelStack.Pop();
		panelLoader.PanelVariable.VariableChange -= PanelVariable_VariableChange;
		panelLoader.ChangePanel(panelNodeId, NodeId.Empty);
		panelLoader.PanelVariable.VariableChange += PanelVariable_VariableChange;
	}

	private Stack<NodeId> oldPanelStack;
}
