#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.Retentivity;
using FTOptix.UI;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.ODBCStore;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
#endregion

public class NodesCounterDialogLogic : BaseNetLogic
{
    /// <summary>
    /// This method sets the current page based on the root element and target element.
    /// If the target element is a PanelLoader or NavigationPanel, it retrieves the "CurrentPanel" variable from the target element.
    /// Otherwise, it uses the root element alias.
    /// It also subscribes to the VariableChange event of the current page and calls the CountNodesInPage method.
    /// </summary>
    public override void Start()
    {
        // Using an Alias we fetch the element where we need to count the nodes
        var rootElementAlias = Owner.GetVariable("RootElement") ?? throw new CoreConfigurationException("Alias 'RootElement' not found in the DialogBox");
        // The target of the Alias is the value of the Alias variable (which is a NodeId)
        var rootElement = rootElementAlias.Value ?? throw new CoreConfigurationException("'RootElement' alias was empty, cannot calculate nodes");
        // Get the target element from the Information Model (using the NodeId from the Alias)
        var targetElement = InformationModel.Get(rootElement) ?? throw new CoreConfigurationException("Cannot find the target element in the Information Model");

        // If the target element is a PanelLoader or NavigationPanel, we need to get the current panel so we can listen to changes
        if (targetElement is PanelLoader || targetElement is NavigationPanel)
            currentPage = targetElement.GetVariable("CurrentPanel");
        else
            currentPage = rootElementAlias;

        // Listen to changes in the current page variable
        // When the source node is a PanelLoader or NavigationPanel,
        // the CurrentPanel variable is changed when the user navigates to a different page
        currentPage.VariableChange += CurrentPage_VariableChange;
        CountNodesInPage();
    }

    private void StoreCounts()
    {
        try 
        {
            // Get the embedded db from the project
            var embeddedStore = Project.Current.Get<Store>("DataStores/SQLocal");
            if (embeddedStore == null)
            {
                Log.Error("StoreCounts", "SQLocal not found in DataStores");
                return;
            }

            // Get the node count table
            var nodeCountTable = embeddedStore.Tables.Get<Table>("NodeCounts");
            if (nodeCountTable == null)
            {
                Log.Error("StoreCounts", "NodeCounts table not found in SQLocal");
                return;
            }

            // Prepare the column names and values for the insert 
            string[] columns = { "RunDateTime", "Total", "UIObjects", "Variables", "DynamicLinks", "Converters" };
            var values = new object [1, 6]; 
            values[0, 0] = DateTime.Now; // RunDateTime
            values[0, 1] = nodesCount; // Total
            values[0, 2] = uiObjectsCount; // UIObjects
            values[0, 3] = variablesCount; // Variables
            values[0, 4] = dynamicLinksCount; // DynamicLinks
            values[0, 5] = convertersCount; // Converters

            // insert the counts into the table
            Log.Info($"Storing counts in the database: Total={nodesCount}");
            nodeCountTable.Insert(columns, values);

        }

        catch (Exception ex)
        {
            Log.Error($"Error storing counts: {ex.Message}");
        }
    }

    /// <summary>
    /// This method is triggered when a variable change event occurs, and it calls the method to count the number of nodes in the current page.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The VariableChangeEventArgs instance that contains the event data.</param>
    private void CurrentPage_VariableChange(object sender, VariableChangeEventArgs e)
    {
        // When the current page changes, count the nodes in the new page
        CountNodesInPage();
    }

    public override void Stop()
    {
        // Stop listening to changes in the current page variable
        currentPage.VariableChange -= CurrentPage_VariableChange;
    }

    /// <summary>
    /// This method counts the number of nodes, UI objects, variables, dynamic links, and converters in the current page.
    /// It recursively traverses the root element and updates the counts based on the elements found.
    /// </summary>
    /// <remarks>
    /// The method initializes the count variables and then recursively processes each child element of the root element.
    /// After processing, it updates the labels in the owner with the current counts.
    /// </remarks>
    [ExportMethod]
    public void CountNodesInPage()
    {
        // Get the root element from the Information Model of the project
        var rootElement = InformationModel.Get(currentPage.Value) ?? throw new CoreConfigurationException("Cannot find the target element in the Information Model");

        // Reset counters
        nodesCount = 1;
        uiObjectsCount = 1;
        variablesCount = 0;
        dynamicLinksCount = 0;
        convertersCount = 0;

        // Recursively count child nodes of the root element
        foreach (var child in rootElement.Children)
        {
            RecursivelyCountNodes(child);
        }
        
        // Store the count data in the embedded database
        StoreCounts();
        
        // Set results to UI
        Label nodesCountValue = Owner.Get<Label>("Content/NodesCount/NodesCountValue");
        Label uiObjects = Owner.Get<Label>("Content/Details/ObjectsCountValue");
        Label variables = Owner.Get<Label>("Content/Details/VariablesCountValue");
        Label dynamicLinks = Owner.Get<Label>("Content/Details/DynamicLinksCountValue");
        Label converters = Owner.Get<Label>("Content/Details/ConvertersCountValue");

        nodesCountValue.Text = nodesCount.ToString();
        uiObjects.Text = uiObjectsCount.ToString();
        variables.Text = variablesCount.ToString();
        dynamicLinks.Text = dynamicLinksCount.ToString();
        converters.Text = convertersCount.ToString();
    }

    /// <summary>
    /// Recursively counts the number of nodes in a tree structure starting from the given element.
    /// This method increments a global counter `nodesCount` for each node encountered.
    /// It also tracks the number of UI objects, variables, dynamic links, and converters.
    /// </summary>
    /// <param name="element">The current node being processed.</param>
    private void RecursivelyCountNodes(IUANode element)
    {
        // The head node is also counted
        ++nodesCount;

        if (element is BaseUIObject)
        {
            // Count UI objects
            ++uiObjectsCount;
        }

        if (element.NodeClass == NodeClass.Variable)
        {
            // Count variables
            ++variablesCount;
        }

        // Count dynamic links
        if (element.Refs.GetNode(FTOptix.CoreBase.ReferenceTypes.HasDynamicLink) != null)
        {
            ++dynamicLinksCount;
        }

        // Count converters
        if (element.Refs.GetNode(FTOptix.CoreBase.ReferenceTypes.HasConverter) != null)
        {
            ++convertersCount;
        }

        if (element.Children.Count > 0)
        {
            // Go down recursively
            foreach (var child in element.Children)
                RecursivelyCountNodes(child);
        }
    }

    private IUAVariable currentPage;
    private int nodesCount;
    private int uiObjectsCount;
    private int variablesCount;
    private int dynamicLinksCount;
    private int convertersCount;
}
