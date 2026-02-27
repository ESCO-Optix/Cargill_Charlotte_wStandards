# Cargill Standard - NetSolution

This folder contains the C# NetLogic scripts for the Cargill Standard FactoryTalk Optix project.

## Table of Contents

- [Overview](#overview)
- [UpdateInstances](#updateinstances)
- [PopulateStoreTables](#populatestoretables)
- [Other Components](#other-components)
- [Sample Data Structure](#sample-data-structure)

---

## Overview

The NetSolution folder contains custom C# logic that extends the FactoryTalk Optix functionality. These scripts handle various automation tasks for both design time and run time:

- Automatic menu generation
- PLC tag to model mapping
- User management
- Dynamic UI updates

---

## UpdateInstances

### Purpose

The `UpdateInstances` NetLogic automatically discovers and maps PLC controller tags to model object instances based on matching device types. This eliminates the need to manually configure model objects and their corresponding PLC tags.

### Configuration

The UpdateInstances logic requires two NodeId variables to be configured:

| Variable | Description | Example Path |
|----------|-------------|--------------|
| `PathToTags` | NodeId pointing to the folder containing PLC controller tags | `/Objects/Cargill_Standard/CommDrivers/RA_ENIP/Cargill_Standard/Tags/Controller Tags` |
| `PathToTypes` | NodeId pointing to the folder containing object type definitions | `/Objects/Cargill_Standard/Model/ObjectTypes` |

### How It Works

1. **Build Device Type Mapping**: Scans object types in `PathToTypes` for any type containing a "Device" Pointer. Extracts the "Kind" property to determine what PLC DataType this object type expects. 

2. **Scan PLC Tags**: Recursively scans all tags under `PathToTags` looking for tags with DataType matching the device types from step 1.

3. **Extract Device Names**: Uses the tag name to determine the base device name, stripping common suffixes like `_Ilock`, `_Perm`, `_Sim`, etc.

4. **Handle Nested Types**: Detects device tags nested within container UDTs (e.g., `XV_888888_88.Valve` where the parent UDT contains a `CargillDevice_Valve` child).

5. **Log Results**: Reports all discovered devices grouped by object type.

### Supported Object Types

| Object Type | Expected Device DataType | Description |
|-------------|-------------------------|-------------|
| `Cgl_Valve_PI` | `CargillDevice_Valve` | Valve with Interlock & Permissive |
| `Cgl_Motor_PI` | `CargillDevice_Motor` | Motor with Interlock & Permissive |
| `Cgl_AnalogInput` | `CargillDevice_AnalogIn` | Analog input transmitter |


### Related Tag Suffixes

Tags with these suffixes are grouped with their base device:

- `_Ilock` / `_ILock` - Interlock configuration
- `_Perm` - Permissive configuration  
- `_Sim` - Simulation settings
- `_IO` - I/O mapping
- `_Valve` - Valve-specific settings (for container UDTs)
- `_Module` - Module configuration
- `_Ovld` / `_Ovld_I` - Overload settings

### Example Output (Sample Data)

When executed against the provided SampleTags data, the logic identifies the following devices:

```
Step 1: Building device type mapping from object types...
Found 3 device type mappings:
  - CargillDevice_Valve -> Cgl_Valve_PI
  - CargillDevice_Motor -> Cgl_Motor_PI
  - CargillDevice_AnalogIn -> Cgl_AnalogInput

Step 2: Scanning tags for matching devices...

Step 3: Discovered devices summary:
  Cgl_Valve_PI: 5 device(s) found
    - XV_000000_00 (valve): 1 related tag(s)
    - XV_888888_88 (valve): 1 related tag(s)
    - XV_999999_01 (valve): 1 related tag(s)
    - XV_999999_99 (valve): 1 related tag(s)
    - TestValve (valve): 1 related tag(s)

  Cgl_Motor_PI: 2 device(s) found
    - P000001 (motor): 1 related tag(s)
    - P999999 (motor): 1 related tag(s)

  Cgl_AnalogInput: 3 device(s) found
    - AT_999999_99 (analoginput): 1 related tag(s)
    - MyAnalogDevice_01 (device): 1 related tag(s)
    - TT_000000_01 (analoginput): 1 related tag(s)

Total devices discovered: 10
```

### Usage

1. Configure `PathToTags` and `PathToTypes` variables in the IDE
2. Call the `UpdateModelInstances()` method (can be triggered by button click or startup)
3. Check the Output log for discovered devices

### Error Handling

The logic includes comprehensive error handling:

- Validates that PathToTags and PathToTypes are configured and point to valid nodes
- Skips object types that don't have a Device pointer
- Continues processing even if individual tags fail
- Logs warnings for missing or misconfigured properties
- Uses debug-level logging for detailed troubleshooting

### Best Practices

1. **Consistent Naming**: Ensure PLC tags follow the standard naming conventions for automatic detection
2. **Proper Type Definitions**: Object types must have a "Device" NodePointer with a "Kind" property set to the expected DataType path
3. **Run After Tag Import**: Execute this logic after importing or updating PLC tags
4. **Check Logs**: Review the log output to verify all expected devices were discovered
5. **Test with Sample Data**: Use the provided SampleTags and SampleModel folders to validate behavior

---

## PopulateStoreTables

### Purpose

`PopulateStoreTables` is a **design-time NetLogic** that connects directly to ODBC databases (via `System.Data.Odbc`) and creates or updates Table / Column nodes in the Optix information model. This allows design-time configuration to reference real database columns without running the application.

The Optix `Store.Query()` API is not available at design time, so this script bypasses it entirely by reading the DSN and credentials from the store node and opening its own ODBC connection.

### Prerequisites

- A matching **ODBC DSN** must be configured in the Windows ODBC Data Source Administrator (64-bit) on the machine running Optix Studio.
- The `System.Data.Odbc` NuGet package (v8.0.1) is referenced in `Cargill_Standard.csproj`.

### Exported Methods

| Method | Description |
|--------|-------------|
| `PopulateCharlotte()` | Populates tables/columns for the **Charlotte** ODBC store only. |
| `PopulateAllOdbcStores()` | Iterates every `ODBCStore` under `DataStores/` and populates each. |

### SQL-to-OPC UA Type Mapping

| SQL Type(s) | OPC UA Type |
|-------------|-------------|
| BIT | Boolean |
| TINYINT | Byte |
| SMALLINT | Int16 |
| INT, INTEGER | Int32 |
| BIGINT | Int64 |
| REAL, FLOAT | Float |
| DOUBLE | Double |
| DECIMAL, NUMERIC, MONEY | Double |
| DATE, DATETIME, DATETIME2, SMALLDATETIME | DateTime |
| CHAR, NCHAR, VARCHAR, NVARCHAR, TEXT, NTEXT, XML | String |
| BINARY, VARBINARY, IMAGE | ByteString |
| UNIQUEIDENTIFIER, TIME | String |

---

## Other Components

### FromPlcToModel.cs
Design-time NetLogic that generates model variables/objects based on imported PLC tags structure.

### MenuSetup.cs  


---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.1 | 2026-02-15 | Added PopulateStoreTables design-time NetLogic |
| 1.0 | 2026-02-01 | Initial UpdateInstances implementation |

---

## Support

For issues or questions, contact the Cargill Automation team.