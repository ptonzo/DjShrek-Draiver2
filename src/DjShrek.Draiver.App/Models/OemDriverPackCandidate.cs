namespace DjShrek.Draiver.App.Models;

/// <summary>
/// A model-level OEM driver pack discovered in a manufacturer's machine-readable catalog.
/// It is not an individual device update and must not be installed without inspecting its contents.
/// </summary>
public sealed record OemDriverPackCandidate(
    string Manufacturer,
    string Model,
    string MachineType,
    string OperatingSystem,
    string CatalogVersion,
    string PackageVersion,
    string ReleaseDate,
    Uri PackageUri,
    string CatalogSource,
    string CompatibilityEvidence);
