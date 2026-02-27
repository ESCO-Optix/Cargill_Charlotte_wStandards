#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.ODBCStore;
using FTOptix.Report;
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using FTOptix.System;
using FTOptix.EventLogger;
using FTOptix.DataLogger;
using FTOptix.Alarm;
using FTOptix.SerialPort;
using FTOptix.SQLiteStore;
using FTOptix.AuditSigning;
#endregion

/// <summary>
/// UpdateInstances NetLogic - Automatically maps PLC tags to model object instances
/// based on matching device types between object types and controller tags.
/// 
/// OVERVIEW:
/// This logic scans PLC tags and matches them to object types by comparing the 
/// DataType of tags against the "Kind" property of "Device" NodePointers in object types.
/// It then creates instances of the object types with NodePointers configured to the matching tags.
/// 
/// CONFIGURATION:
/// - PathToTags: NodeId pointing to the folder containing PLC controller tags
/// - PathToTypes: NodeId pointing to the folder containing object type definitions
/// - PathToInstances: NodeId pointing to the folder where instances will be created (Model/Instances)
/// 
/// HOW IT WORKS:
/// 1. Reads object types from PathToTypes folder
/// 2. For each object type with a "Device" NodePointer, extracts the expected DataType (Kind)
/// 3. Scans all tags in PathToTags recursively
/// 4. Matches tags by DataType to determine device type
/// 5. Groups related tags by base device name (ignoring suffixes like _Ilock, _Perm, etc.)
/// 6. Creates instances in PathToInstances organized by category folders:
///    - Valves/ (XV_, HV_, PV_, FV_, CV_, TV_)
///    - Motors/ (P followed by digit)
///    - AnalogInputs/ (FT_, TT_, PT_, LT_, AT_, WT_, DT_, ST_)
///    - Switches/ (LSH_, LSL_, PSH_, PSL_, TSH_, TSL_)
///    - Devices/ (other)
/// 
/// SUPPORTED DEVICE TYPES (based on ObjectTypes):
/// - Cgl_Valve_PI -> CargillDevice_Valve
/// - Cgl_Motor_PI -> CargillDevice_Motor  
/// - Cgl_AnalogInput -> CargillDevice_AnalogIn
/// </summary>
public class UpdateInstances : BaseNetLogic
{
    #region Constants
    
    /// <summary>
    /// Name of the NodePointer variable within object types that references the device tag.
    /// </summary>
    private const string DEVICE_POINTER_NAME = "Device";

    /// <summary>
    /// Name of the property within Device NodePointer that specifies the expected tag type.
    /// </summary>
    private const string KIND_PROPERTY_NAME = "Kind";

    /// <summary>
    /// Common suffixes appended to device names for related tags (interlocks, permissives, etc.)
    /// These are removed when extracting the base device name.
    /// Order by length descending to match longest suffix first.
    /// </summary>
    private static readonly string[] DEVICE_SUFFIXES = new string[]
    {
        "_Ilock", "_ILock", "_Perm", "_Sim", "_IO", "_Valve", "_Module", "_Ovld_I", "_Ovld"
    };
    
    #endregion

    #region Private Fields
    
    /// <summary>
    /// Task for running the update operation asynchronously.
    /// </summary>
    private LongRunningTask updateTask;

    /// <summary>
    /// Reference to the tags folder node.
    /// </summary>
    private IUANode tagsNode;

    /// <summary>
    /// Reference to the types folder node.
    /// </summary>
    private IUANode typesNode;

    /// <summary>
    /// Reference to the instances folder node (Model/Instances).
    /// </summary>
    private IUANode instancesNode;

    /// <summary>
    /// Counter for instances created in current run.
    /// </summary>
    private int instancesCreated;

    /// <summary>
    /// Dictionary mapping device DataType names to their corresponding object type information.
    /// Key: DataType name (e.g., "CargillDevice_Valve")
    /// Value: ObjectTypeInfo containing type metadata and NodePointer children names
    /// </summary>
    private Dictionary<string, ObjectTypeInfo> deviceTypeMapping;

    /// <summary>
    /// Dictionary storing discovered devices grouped by object type.
    /// Key: Object type name (e.g., "Cgl_Valve_PI")
    /// Value: Dictionary of device name -> DeviceInfo with all related tags
    /// </summary>
    private Dictionary<string, Dictionary<string, DeviceInfo>> discoveredDevices;
    
    #endregion

    #region Helper Classes
    
    /// <summary>
    /// Holds information about an object type and its NodePointer children.
    /// </summary>
    private class ObjectTypeInfo
    {
        /// <summary>
        /// The name of the object type (e.g., "Cgl_Valve_PI").
        /// </summary>
        public string TypeName { get; set; }
        
        /// <summary>
        /// The object type node reference.
        /// </summary>
        public IUANode TypeNode { get; set; }
        
        /// <summary>
        /// The device DataType name this type expects (e.g., "CargillDevice_Valve").
        /// </summary>
        public string DeviceDataType { get; set; }
        
        /// <summary>
        /// Dictionary of NodePointer names to their expected Kind types.
        /// E.g., {"Device" -> "CargillDevice_Valve", "Interlock" -> "CargillFunction_Interlock"}
        /// </summary>
        public Dictionary<string, string> NodePointerTypes { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Holds information about a discovered device and its related tags.
    /// </summary>
    private class DeviceInfo
    {
        /// <summary>
        /// The base device name (e.g., "XV_999999_99").
        /// </summary>
        public string BaseName { get; set; }
        
        /// <summary>
        /// The main device tag node.
        /// </summary>
        public IUANode DeviceTag { get; set; }
        
        /// <summary>
        /// Dictionary of related tags by their suffix/role.
        /// E.g., {"Device" -> tagNode, "Interlock" -> ilockTagNode, "Permissive" -> permTagNode}
        /// </summary>
        public Dictionary<string, IUANode> RelatedTags { get; set; } = new Dictionary<string, IUANode>();
        
        /// <summary>
        /// The determined category of this device (valve, motor, analoginput, etc.).
        /// </summary>
        public string Category { get; set; }
    }
    
    #endregion

    #region Public Methods
    
    /// <summary>
    /// Main entry point - initiates the model instance update process.
    /// This method starts a long-running task to scan tags and match them to object types.
    /// </summary>
    [ExportMethod]
    public void UpdateModelInstances()
    {
        try
        {
            // Validate and get PathToTags
            var pathToTagsVar = LogicObject.GetVariable("PathToTags");
            if (pathToTagsVar == null || pathToTagsVar.Value == null)
            {
                Log.Error(GetType().Name, "PathToTags variable is not configured. Please set the NodeId pointing to the controller tags folder.");
                return;
            }

            NodeId tagsNodeId = pathToTagsVar.Value.Value as NodeId;
            if (tagsNodeId == null || tagsNodeId.IsEmpty)
            {
                Log.Error(GetType().Name, "PathToTags NodeId is empty or invalid.");
                return;
            }

            tagsNode = InformationModel.Get(tagsNodeId);
            if (tagsNode == null)
            {
                Log.Error(GetType().Name, $"Cannot find node at PathToTags: {tagsNodeId}");
                return;
            }

            // Validate and get PathToTypes
            var pathToTypesVar = LogicObject.GetVariable("PathToTypes");
            if (pathToTypesVar == null || pathToTypesVar.Value == null)
            {
                Log.Error(GetType().Name, "PathToTypes variable is not configured. Please set the NodeId pointing to the object types folder.");
                return;
            }

            NodeId typesNodeId = pathToTypesVar.Value.Value as NodeId;
            if (typesNodeId == null || typesNodeId.IsEmpty)
            {
                Log.Error(GetType().Name, "PathToTypes NodeId is empty or invalid.");
                return;
            }

            typesNode = InformationModel.Get(typesNodeId);
            if (typesNode == null)
            {
                Log.Error(GetType().Name, $"Cannot find node at PathToTypes: {typesNodeId}");
                return;
            }

            // Validate and get PathToInstances
            var pathToInstancesVar = LogicObject.GetVariable("PathToInstances");
            if (pathToInstancesVar == null || pathToInstancesVar.Value == null)
            {
                Log.Error(GetType().Name, "PathToInstances variable is not configured. Please set the NodeId pointing to the Model/Instances folder.");
                return;
            }

            NodeId instancesNodeId = pathToInstancesVar.Value.Value as NodeId;
            if (instancesNodeId == null || instancesNodeId.IsEmpty)
            {
                Log.Error(GetType().Name, "PathToInstances NodeId is empty or invalid.");
                return;
            }

            instancesNode = InformationModel.Get(instancesNodeId);
            if (instancesNode == null)
            {
                Log.Error(GetType().Name, $"Cannot find node at PathToInstances: {instancesNodeId}");
                return;
            }

            Log.Info(GetType().Name, "========================================");
            Log.Info(GetType().Name, "Starting UpdateModelInstances...");
            Log.Info(GetType().Name, $"PathToTags: {Log.Node(tagsNode)}");
            Log.Info(GetType().Name, $"PathToTypes: {Log.Node(typesNode)}");
            Log.Info(GetType().Name, $"PathToInstances: {Log.Node(instancesNode)}");
            Log.Info(GetType().Name, "========================================");

            // Initialize data structures
            deviceTypeMapping = new Dictionary<string, ObjectTypeInfo>();
            discoveredDevices = new Dictionary<string, Dictionary<string, DeviceInfo>>();
            instancesCreated = 0;

            // Start long-running task to avoid blocking the UI
            updateTask = new LongRunningTask(UpdateModelInstancesTask, LogicObject);
            updateTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, $"Error starting UpdateModelInstances: {ex.Message}");
            Log.Error(GetType().Name, $"Stack trace: {ex.StackTrace}");
        }
    }
    
    #endregion

    #region Private Methods - Main Task
    
    /// <summary>
    /// Long-running task that performs the actual device discovery and mapping.
    /// </summary>
    private void UpdateModelInstancesTask()
    {
        try
        {
            // Step 1: Build mapping from device DataTypes to object types
            BuildDeviceTypeMapping();

            if (deviceTypeMapping.Count == 0)
            {
                Log.Warning(GetType().Name, "No object types with Device pointers found in PathToTypes.");
                return;
            }

            // Step 2: Scan tags and discover devices
            ScanTagsRecursively(tagsNode, "");

            // Step 3: Match related tags (Interlock, Permissive) to devices
            MatchRelatedTags();

            // Step 4: Create model instances
            CreateModelInstances();

            // Step 5: Log summary
            LogCreatedInstances();

            Log.Info(GetType().Name, "========================================");
            Log.Info(GetType().Name, $"UpdateModelInstances completed: {instancesCreated} instance(s) created.");
            Log.Info(GetType().Name, "========================================");
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, $"Error in UpdateModelInstancesTask: {ex.Message}");
            Log.Error(GetType().Name, $"Stack trace: {ex.StackTrace}");
        }
        finally
        {
            updateTask?.Dispose();
        }
    }
    
    #endregion

    #region Private Methods - Device Type Mapping
    
    /// <summary>
    /// Builds the mapping from device DataType names to object types by scanning
    /// the object types folder and extracting Device pointer Kind properties.
    /// </summary>
    private void BuildDeviceTypeMapping()
    {
        foreach (var child in typesNode.Children)
        {
            try
            {
                ProcessObjectType(child);
            }
            catch (Exception ex)
            {
                Log.Warning(GetType().Name, $"Error processing object type '{child.BrowseName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Processes a single object type to extract its device type mapping.
    /// </summary>
    /// <param name="objectTypeNode">The object type node to process</param>
    private void ProcessObjectType(IUANode objectTypeNode)
    {
        // Look for a "Device" child node (NodePointer)
        var devicePointer = objectTypeNode.Get(DEVICE_POINTER_NAME);
        if (devicePointer == null)
            return;

        // Get the Kind property which specifies the expected device DataType
        string deviceTypeName = GetNodePointerKind(devicePointer);
        if (string.IsNullOrEmpty(deviceTypeName))
            return;

        // Create ObjectTypeInfo
        var typeInfo = new ObjectTypeInfo
        {
            TypeName = objectTypeNode.BrowseName,
            TypeNode = objectTypeNode,
            DeviceDataType = deviceTypeName
        };

        // Scan for all NodePointer children to understand the full structure
        foreach (var child in objectTypeNode.Children)
        {
            if (IsNodePointer(child))
            {
                string kindType = GetNodePointerKind(child);
                if (!string.IsNullOrEmpty(kindType))
                {
                    typeInfo.NodePointerTypes[child.BrowseName] = kindType;
                }
            }
        }

        // Add to mapping
        deviceTypeMapping[deviceTypeName] = typeInfo;
        discoveredDevices[objectTypeNode.BrowseName] = new Dictionary<string, DeviceInfo>();
    }

    /// <summary>
    /// Checks if a node is a NodePointer type.
    /// </summary>
    private bool IsNodePointer(IUANode node)
    {
        try
        {
            // Check if the node has a Kind child which is characteristic of NodePointer
            return node.Get(KIND_PROPERTY_NAME) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the Kind value from a NodePointer node.
    /// </summary>
    /// <param name="nodePointer">The NodePointer node</param>
    /// <returns>The type name from the Kind property, or empty string if not found</returns>
    private string GetNodePointerKind(IUANode nodePointer)
    {
        try
        {
            var kindProperty = nodePointer.Get(KIND_PROPERTY_NAME);
            if (kindProperty == null)
                return string.Empty;

            string kindValue = null;

            if (kindProperty is IUAVariable kindVar)
            {
                var value = kindVar.Value?.Value;
                if (value != null)
                {
                    // Handle NodeId type - need to resolve to get the BrowseName
                    if (value is NodeId nodeId)
                    {
                        var resolvedNode = InformationModel.Get(nodeId);
                        kindValue = resolvedNode?.BrowseName ?? nodeId.ToString();
                    }
                    else
                    {
                        kindValue = value.ToString();
                    }
                }
            }

            if (string.IsNullOrEmpty(kindValue))
                return string.Empty;

            return ExtractTypeNameFromPath(kindValue);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Extracts the type name from a node path or node ID string.
    /// </summary>
    /// <param name="path">The path string (e.g., "/Objects/Cargill_Standard/Model/CargillDevice_Valve")</param>
    /// <returns>The extracted type name (e.g., "CargillDevice_Valve")</returns>
    private string ExtractTypeNameFromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // Handle path format: /Objects/Cargill_Standard/Model/CargillDevice_Valve
        if (path.Contains("/"))
        {
            string[] segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[segments.Length - 1] : string.Empty;
        }

        // Handle NodeId format or simple name
        return path;
    }
    
    #endregion

    #region Private Methods - Tag Scanning
    
    /// <summary>
    /// Recursively scans tags looking for matching device types.
    /// </summary>
    /// <param name="node">The current node to scan</param>
    /// <param name="parentPath">The path to the parent for logging purposes</param>
    private void ScanTagsRecursively(IUANode node, string parentPath)
    {
        foreach (var child in node.Children)
        {
            try
            {
                string currentPath = string.IsNullOrEmpty(parentPath) ? child.BrowseName : $"{parentPath}/{child.BrowseName}";
                
                // Check if this node has a DataType that matches one of our device types
                ProcessTagNode(child, currentPath);

                // Recursively scan children (folders, nested structures)
                if (child.Children.Count > 0)
                {
                    ScanTagsRecursively(child, currentPath);
                }
            }
            catch
            {
                // Skip nodes that can't be processed
            }
        }
    }

    /// <summary>
    /// Processes a single tag node to determine if it matches a known device type.
    /// </summary>
    /// <param name="node">The tag node to process</param>
    /// <param name="path">The full path to this node for logging</param>
    private void ProcessTagNode(IUANode node, string path)
    {
        // Get the DataType/Type of this node
        string dataTypeName = GetNodeTypeName(node);

        if (string.IsNullOrEmpty(dataTypeName))
            return;

        // Check if this DataType matches any of our device types
        if (deviceTypeMapping.TryGetValue(dataTypeName, out var typeInfo))
        {
            // Extract the base device name
            string baseName = ExtractBaseDeviceName(node.BrowseName);
            
            // Skip generic/nested child names that aren't real device names
            if (IsGenericChildName(baseName))
                return;
            
            string category = DetermineDeviceCategory(baseName);

            // Add to discovered devices
            if (!discoveredDevices[typeInfo.TypeName].ContainsKey(baseName))
            {
                discoveredDevices[typeInfo.TypeName][baseName] = new DeviceInfo
                {
                    BaseName = baseName,
                    DeviceTag = node,
                    Category = category
                };
                discoveredDevices[typeInfo.TypeName][baseName].RelatedTags[DEVICE_POINTER_NAME] = node;
            }
        }

        // Also check for nested device types (e.g., XV_888888_88.Valve has CargillDevice_Valve as child)
        CheckNestedDeviceTypes(node, path);
    }

    /// <summary>
    /// Checks for nested device types within a container UDT.
    /// Some tags are containers (like Cgl_Obj_ValveUDT) that have device children.
    /// </summary>
    /// <param name="parentNode">The parent node to check for nested devices</param>
    /// <param name="parentPath">The path to the parent node</param>
    private void CheckNestedDeviceTypes(IUANode parentNode, string parentPath)
    {
        foreach (var child in parentNode.Children)
        {
            string childTypeName = GetNodeTypeName(child);
            
            if (!string.IsNullOrEmpty(childTypeName) && deviceTypeMapping.TryGetValue(childTypeName, out var typeInfo))
            {
                // The parent name becomes the device name
                string baseName = ExtractBaseDeviceName(parentNode.BrowseName);
                
                // Skip generic/nested child names
                if (IsGenericChildName(baseName))
                    continue;
                
                string category = DetermineDeviceCategory(baseName);

                if (!discoveredDevices[typeInfo.TypeName].ContainsKey(baseName))
                {
                    discoveredDevices[typeInfo.TypeName][baseName] = new DeviceInfo
                    {
                        BaseName = baseName,
                        DeviceTag = child,  // The nested child is the actual device tag
                        Category = category
                    };
                    discoveredDevices[typeInfo.TypeName][baseName].RelatedTags[DEVICE_POINTER_NAME] = child;
                }
            }
        }
    }

    /// <summary>
    /// Gets the Type/DataType name of a node.
    /// </summary>
    /// <param name="node">The node to get the type from</param>
    /// <returns>The type name, or empty string if not applicable</returns>
    private string GetNodeTypeName(IUANode node)
    {
        try
        {
            // For variables with DataType (this covers most PLC tags)
            if (node is IUAVariable variable && variable.DataType != null)
            {
                var dataTypeNode = InformationModel.Get(variable.DataType);
                if (dataTypeNode != null)
                {
                    return dataTypeNode.BrowseName;
                }
            }

            // For objects, try ObjectType
            if (node is IUAObject uaObject)
            {
                var objectType = uaObject.ObjectType;
                if (objectType != null)
                {
                    return objectType.BrowseName;
                }
            }
        }
        catch
        {
            // Ignore errors getting type
        }

        return string.Empty;
    }
    
    /// <summary>
    /// Checks if a name is a generic child name (not a real device identifier).
    /// </summary>
    private bool IsGenericChildName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return true;
            
        // List of generic child names to filter out
        string[] genericNames = { "Valve", "Motor", "Device", "Analog", "Switch", "Pump", 
                                   "TestValve", "TestMotor", "TestDevice", "Test" };
        
        return genericNames.Any(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    
    #endregion

    #region Private Methods - Related Tag Matching
    
    /// <summary>
    /// Matches related tags (Interlock, Permissive, etc.) to discovered devices.
    /// Looks for tags with the device base name plus common suffixes.
    /// </summary>
    private void MatchRelatedTags()
    {
        // Build a lookup of all tags by their browse name for efficient searching
        var tagLookup = new Dictionary<string, IUANode>(StringComparer.OrdinalIgnoreCase);
        BuildTagLookup(tagsNode, tagLookup);

        foreach (var typeGroup in discoveredDevices)
        {
            string typeName = typeGroup.Key;
            var devices = typeGroup.Value;
            
            if (!deviceTypeMapping.TryGetValue(GetDeviceDataTypeForTypeName(typeName), out var typeInfo))
                continue;

            foreach (var device in devices.Values)
            {
                // Look for related tags based on the object type's NodePointer definitions
                foreach (var npDef in typeInfo.NodePointerTypes)
                {
                    if (npDef.Key == DEVICE_POINTER_NAME)
                        continue;  // Already have the device tag

                    // Try to find matching tag with common suffixes
                    IUANode relatedTag = FindRelatedTag(device.BaseName, npDef.Key, npDef.Value, tagLookup);
                    if (relatedTag != null)
                    {
                        device.RelatedTags[npDef.Key] = relatedTag;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets the device DataType name for a given object type name.
    /// </summary>
    private string GetDeviceDataTypeForTypeName(string typeName)
    {
        foreach (var mapping in deviceTypeMapping)
        {
            if (mapping.Value.TypeName == typeName)
                return mapping.Key;
        }
        return string.Empty;
    }

    /// <summary>
    /// Builds a lookup dictionary of all tags by their browse name.
    /// </summary>
    private void BuildTagLookup(IUANode node, Dictionary<string, IUANode> lookup)
    {
        foreach (var child in node.Children)
        {
            // Add this node to lookup (may overwrite if duplicate names)
            if (!lookup.ContainsKey(child.BrowseName))
            {
                lookup[child.BrowseName] = child;
            }

            // Recursively process children
            if (child.Children.Count > 0)
            {
                BuildTagLookup(child, lookup);
            }
        }
    }

    /// <summary>
    /// Finds a related tag for a device based on the NodePointer type.
    /// </summary>
    /// <param name="baseName">The base device name</param>
    /// <param name="pointerName">The NodePointer name (e.g., "Interlock")</param>
    /// <param name="expectedType">The expected tag type</param>
    /// <param name="tagLookup">The tag lookup dictionary</param>
    /// <returns>The matching tag node or null</returns>
    private IUANode FindRelatedTag(string baseName, string pointerName, string expectedType, Dictionary<string, IUANode> tagLookup)
    {
        // Define possible suffixes for each pointer type
        var suffixMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Interlock", new[] { "_Ilock", "_ILock", "_Interlock" } },
            { "Permissive", new[] { "_Perm", "_Permissive" } },
            { "Simulation", new[] { "_Sim", "_Simulation" } },
            { "Controller", new[] { "" } }  // Controller typically doesn't have a suffix
        };

        if (!suffixMappings.TryGetValue(pointerName, out var suffixes))
        {
            suffixes = new[] { $"_{pointerName}" };  // Default: add underscore + pointer name
        }

        foreach (var suffix in suffixes)
        {
            string tagName = baseName + suffix;
            if (tagLookup.TryGetValue(tagName, out var tag))
            {
                // Verify the tag type matches (optional - remove if too strict)
                string tagType = GetNodeTypeName(tag);
                if (string.IsNullOrEmpty(expectedType) || 
                    tagType.Contains(expectedType.Split('_').Last(), StringComparison.OrdinalIgnoreCase))
                {
                    return tag;
                }
            }
        }

        return null;
    }
    
    #endregion

    #region Private Methods - Utility
    
    /// <summary>
    /// Extracts the base device name by removing common suffixes.
    /// For example: "XV_999999_99_Ilock" becomes "XV_999999_99"
    /// </summary>
    /// <param name="tagName">The full tag name</param>
    /// <returns>The base device name</returns>
    private string ExtractBaseDeviceName(string tagName)
    {
        if (string.IsNullOrEmpty(tagName))
            return tagName;

        string result = tagName;

        // Remove known suffixes (try longest first)
        foreach (var suffix in DEVICE_SUFFIXES.OrderByDescending(s => s.Length))
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(0, result.Length - suffix.Length);
                break;  // Only remove one suffix
            }
        }

        return result;
    }

    /// <summary>
    /// Determines the device category based on common naming conventions.
    /// </summary>
    /// <param name="deviceName">The device name to categorize</param>
    /// <returns>A category string (valve, motor, analoginput, switch, device)</returns>
    private string DetermineDeviceCategory(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return "device";

        // Common prefixes for device types (case-insensitive)
        string upper = deviceName.ToUpperInvariant();

        // Valves
        if (upper.StartsWith("XV_") || upper.StartsWith("HV_") || 
            upper.StartsWith("PV_") || upper.StartsWith("FV_") ||
            upper.StartsWith("CV_") || upper.StartsWith("TV_"))
            return "Valves";
        
        // Motors/Pumps (P followed by digit)
        if (upper.StartsWith("P") && deviceName.Length > 1 && char.IsDigit(deviceName[1]))
            return "Motors";
        
        // Analog inputs (transmitters)
        if (upper.StartsWith("FT_") || upper.StartsWith("TT_") || 
            upper.StartsWith("PT_") || upper.StartsWith("LT_") ||
            upper.StartsWith("AT_") || upper.StartsWith("WT_") ||
            upper.StartsWith("DT_") || upper.StartsWith("ST_"))
            return "AnalogInputs";
        
        // Switches
        if (upper.StartsWith("LSH_") || upper.StartsWith("LSL_") ||
            upper.StartsWith("PSH_") || upper.StartsWith("PSL_") ||
            upper.StartsWith("TSH_") || upper.StartsWith("TSL_"))
            return "Switches";

        return "Devices";
    }

    /// <summary>
    /// Gets or creates a category subfolder under the instances folder.
    /// </summary>
    /// <param name="categoryName">The category name (e.g., "Valves", "Motors")</param>
    /// <returns>The folder node for the category</returns>
    private IUANode GetOrCreateCategoryFolder(string categoryName)
    {
        // Check if folder already exists
        var existingFolder = instancesNode.Get(categoryName);
        if (existingFolder != null)
            return existingFolder;

        // Create new folder
        var folder = InformationModel.MakeObject(categoryName, OpcUa.ObjectTypes.FolderType);
        instancesNode.Add(folder);
        return folder;
    }
    
    #endregion

    #region Private Methods - Instance Creation
    
    /// <summary>
    /// Creates model instances for all discovered devices.
    /// Instances are organized in category subfolders under PathToInstances.
    /// </summary>
    private void CreateModelInstances()
    {
        foreach (var typeGroup in discoveredDevices)
        {
            string typeName = typeGroup.Key;
            var devices = typeGroup.Value;

            if (devices.Count == 0)
                continue;

            // Get the object type for creating instances
            if (!deviceTypeMapping.TryGetValue(GetDeviceDataTypeForTypeName(typeName), out var typeInfo))
                continue;

            var objectType = typeInfo.TypeNode as IUAObjectType;
            if (objectType == null)
                continue;

            foreach (var device in devices.Values)
            {
                try
                {
                    CreateDeviceInstance(device, objectType);
                }
                catch (Exception ex)
                {
                    Log.Warning(GetType().Name, $"Failed to create instance for {device.BaseName}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Creates a single device instance in the appropriate category folder.
    /// </summary>
    /// <param name="device">The device information</param>
    /// <param name="objectType">The object type to instantiate</param>
    private void CreateDeviceInstance(DeviceInfo device, IUAObjectType objectType)
    {
        // Get or create the category folder
        var categoryFolder = GetOrCreateCategoryFolder(device.Category);

        // Check if instance already exists
        var existingInstance = categoryFolder.Get(device.BaseName);
        if (existingInstance != null)
        {
            // Update existing instance's NodePointers
            UpdateInstancePointers(existingInstance, device);
            return;
        }

        // Create new instance of the object type
        var instance = InformationModel.MakeObject(device.BaseName, objectType.NodeId);

        // Set NodePointer values for Device and related tags
        SetInstancePointers(instance, device);

        // Add to category folder
        categoryFolder.Add(instance);
        instancesCreated++;

        Log.Info(GetType().Name, $"  Created: {device.BaseName} ({device.Category})");
    }

    /// <summary>
    /// Sets the NodePointer values on a new instance.
    /// </summary>
    /// <param name="instance">The instance node</param>
    /// <param name="device">The device information with related tags</param>
    private void SetInstancePointers(IUANode instance, DeviceInfo device)
    {
        foreach (var relatedTag in device.RelatedTags)
        {
            string pointerName = relatedTag.Key;
            IUANode tagNode = relatedTag.Value;

            var pointer = instance.GetVariable(pointerName);
            if (pointer != null)
            {
                pointer.Value = tagNode.NodeId;
            }
        }
    }

    /// <summary>
    /// Updates NodePointer values on an existing instance.
    /// </summary>
    /// <param name="instance">The existing instance node</param>
    /// <param name="device">The device information with related tags</param>
    private void UpdateInstancePointers(IUANode instance, DeviceInfo device)
    {
        foreach (var relatedTag in device.RelatedTags)
        {
            string pointerName = relatedTag.Key;
            IUANode tagNode = relatedTag.Value;

            var pointer = instance.GetVariable(pointerName);
            if (pointer != null)
            {
                // Only update if different
                var currentValue = pointer.Value?.Value as NodeId;
                if (currentValue == null || !currentValue.Equals(tagNode.NodeId))
                {
                    pointer.Value = tagNode.NodeId;
                }
            }
        }
    }
    
    #endregion

    #region Private Methods - Logging
    
    /// <summary>
    /// Logs a summary of created instances grouped by category.
    /// </summary>
    private void LogCreatedInstances()
    {
        // Group devices by category for summary
        var categoryGroups = discoveredDevices
            .SelectMany(t => t.Value.Values)
            .GroupBy(d => d.Category)
            .OrderBy(g => g.Key);

        Log.Info(GetType().Name, "");
        Log.Info(GetType().Name, "Devices by category:");
        
        foreach (var group in categoryGroups)
        {
            var deviceNames = group.Select(d => d.BaseName).OrderBy(n => n);
            Log.Info(GetType().Name, $"  {group.Key}: {string.Join(", ", deviceNames)}");
        }
    }
    
    #endregion
}
