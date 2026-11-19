namespace EchoEnvironment.Hardware;

/// <summary>
/// A serial port offered by the current transport, for the port picker.
///
/// Deliberately minimal: the only thing the UI does today is list names, order them, and preselect
/// a default. Adding friendly device names or a "is this the sensor?" heuristic would be a feature,
/// not part of this seam. Name is the whole contract.
/// </summary>
public readonly record struct PortDescriptor(string Name);
