#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.OPCUAServer;
using FTOptix.WebUI;
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CommunicationDriver;
using FTOptix.CoreBase;
using FTOptix.Core;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using FTOptix.SQLiteStore;
using FTOptix.System;
using FTOptix.Recipe;
using FTOptix.Store;
using FTOptix.OPCUAClient;
using FTOptix.EdgeAppPlatform;
using FTOptix.ODBCStore;
using FTOptix.Report;
using FTOptix.EventLogger;
using FTOptix.DataLogger;
using FTOptix.Alarm;
using FTOptix.SerialPort;
using FTOptix.AuditSigning;
#endregion

public class Nav_UsingTag : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]

    public void NavTag()
    {
        //constant for library and type location in Logix Controller
        const string libraryTag = "Inf_Lib";      // This will eventually be deleted when all libraries are using Extended Tag Properties
        const string libraryTypeTag = "Inf_Type"; // This will eventually be deleted when all libraries are using Extended Tag Properties
        const string library = "Library";
        const string libraryType = "Instruction";

        //Define strings for library and type to be read from Logix Controller
        string sourceMsg = string.Empty;
    

        string fpEquipType = string.Empty; 

        //Define nodes used
        DialogType dBFromString = null;
        IUANode logixTag = null;
        IUAObject lButton = null;
        IUAObject launchAliasObj = null;
        string faceplateTypeName = "";

        //  Find the button owner object and the Ref_* tags associated with it.
        try
        {
            // Get button object
            lButton = Owner.Owner.GetObject(this.Owner.BrowseName);

            // Make Launch Object that will contain aliases
            launchAliasObj = InformationModel.MakeObject("LaunchAliasObj");

            // Get each alias from Launch Button and add them into Launch Object, and assign NodeId values 
            foreach (var inpTag in lButton.Children)
            {
                if (inpTag.BrowseName.Contains("Ref_"))
                {
                    try
                    {   
                        // Make a variable with same name as alias of type NodeId
                        var newVar = InformationModel.MakeVariable(inpTag.BrowseName, OpcUa.DataTypes.NodeId);
                        // Assign alias value to new variable
                        newVar.Value = ((UAManagedCore.UAVariable)inpTag).Value;

                        // Add variable int launch object
                        launchAliasObj.Add(newVar);
                        
                        if (inpTag.BrowseName == "Ref_Tag")
                        {
                            logixTag = InformationModel.Get(((UAVariable)inpTag).Value);
                        }
                    }
                    catch
                    {
                        if (inpTag.BrowseName == "Ref_Tag")
                        {
                            Log.Warning(this.GetType().Name, "Unable to create alias for object '" + inpTag.BrowseName + "'");
                            return;
                        }
                        else
                        {
                            Log.Info(this.GetType().Name, "Skipping alias creation for object '" + inpTag.BrowseName + "'");
                        }
                    }

                }
            }
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Error creating alias Ref_* objects");
            return;
        }

        // Make sure the Logix Tag is valid before continuing
        if (logixTag == null)
        {
            Log.Warning(this.GetType().Name, "Failed to get logix tag from Ref_* objects");
            return;
        }

        // Retrieve the display type
        string fpType;
        try
        {
            fpType = lButton.GetVariable("Cfg_DisplayType").Value; //TODO: This line would call up "faceplate, advanced, quick" we can likely just use this to call the actual type, valve, motor, switch, em ect. 
            fpType = "Faceplate"; // Hard coding faceplate in for now. 
        }
        catch
        {
            //Log.Warning(this.GetType().Name, "Failed to read Optix variable 'Cfg_DisplayType'");
            fpType = "Faceplate"; // Hard coding faceplate in for now. 
        }


        try
        {
            fpEquipType = lButton.GetVariable("Cfg_EquipType").Value;
            /* Old rockwell code 
            lib = (string)logixTag.Children.GetVariable(libraryTag).RemoteRead().Value;
            lType = (string)logixTag.Children.GetVariable(libraryTypeTag).RemoteRead().Value;
            sourceMsg = "Check tag members '" + libraryTag + "' (" + lib + ") and '" + libraryTypeTag + "' (" + lType + ")"; 
            */ 
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Failed to read identity tags for object '" + logixTag.BrowseName + "'. Object must contain Extended Tag Properties '" + library + "' and '" + libraryType + "' or tags '" + libraryTag + "' and '" + libraryTypeTag + "'");
            return;
        }


        // Build the dialog box name and return the object
    
        try
        {
            /* All this does is some sting work and a search to find the dialog box we want to launch, if we just have the equipment display
            have a variable that is the 'type' of object we are clicking, we can pass that.
            Then concat with FP_{type} or what ever
            */ 
            //faceplateTypeName = lib.Replace('-', '_') + '_' + lType + '_' + fpType;
            faceplateTypeName = fpType + "_" + fpEquipType;

            // Find DialogBox from assembled Faceplate string
            var foundFp = Project.Current.Find(faceplateTypeName);
            if ( foundFp == null )
            {
                Log.Warning(this.GetType().Name, "Dialog Box '" + faceplateTypeName + "' not found for tag '" + logixTag.BrowseName + "'. " + sourceMsg);
                return;
            }

            // if found is DialogType, than it is a faceplate type
            if (foundFp.GetType() == typeof(DialogType))
            {
                dBFromString = (DialogType)foundFp;
            }
            else // found current instance of faceplate
            {
                // Get faceplate type from instance
                System.Reflection.PropertyInfo objType = foundFp.GetType().GetProperty("ObjectType");
                dBFromString = (DialogType)(objType.GetValue(foundFp, null));
            }
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Error retrieving Dialog Box for tag '" + logixTag.BrowseName + "'. " + sourceMsg);
            return;
        }


        // Launch the faceplate
        try
        {
            // Launch DialogBox passing Launch Object that contains the aliases as an alias 
            UICommands.OpenDialog(lButton, dBFromString, launchAliasObj.NodeId);
            Log.Warning(this.GetType().Name, "Launched dialog box '" + faceplateTypeName + "' for tag '" + logixTag.BrowseName + "'.");
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Failed to launch dialog box '" + faceplateTypeName + "' for tag '" + logixTag.BrowseName + "'.");
            return;
        }


        // If configured, close the dialog box containing launch button
        try
        {
            bool cfgCloseCurrent = lButton.GetVariable("Cfg_CloseCurrentDisplay").Value;
            if (cfgCloseCurrent)
            {
                CloseCurrentDB(Owner);
            }
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Failed to close current dialog box");
        }
    }

    public void CloseCurrentDB(IUANode inputNode)
    {
        // if input node is of type Dialog, close it
        if (inputNode.GetType().BaseType.BaseType == typeof(Dialog))
        {
            // close dialog box
            ((Dialog)inputNode).Close();
            return;
        }
        // if input node is Main Window, no dialog box was found, return
        if (inputNode.GetType() == typeof(MainWindow))
        {
            return;
        }
        // continue search for Dialog or Main Window
        CloseCurrentDB(inputNode.Owner);
    }
}
