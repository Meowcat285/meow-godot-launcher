using System.Text.Json.Serialization;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Settings;

namespace MeowGodotLauncher.Core;

// Source generated so serialization keeps working in trimmed builds.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(DeltaDescriptor))]
[JsonSerializable(typeof(List<ReleaseInfo>))]
[JsonSerializable(typeof(ReleaseCache))]
[JsonSerializable(typeof(InstalledBuild))]
[JsonSerializable(typeof(LauncherSettings))]
internal sealed partial class LauncherJsonContext : JsonSerializerContext;
