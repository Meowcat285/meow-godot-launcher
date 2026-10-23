using CommunityToolkit.Mvvm.ComponentModel;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.ViewModels;

/// <summary>A checkbox for an export template platform.</summary>
public partial class TemplateComponentItemViewModel(TemplateComponent component, long? size, Action? selectionChanged = null) : ViewModelBase
{
    public TemplateComponent Component { get; } = component;

    public string Id => Component.Id;

    public string Name => Component.Name;

    public string Description => Component.Description;

    /// <summary>Download size of the component's files, when known.</summary>
    public long Size { get; } = size ?? 0;

    public string SizeText => size is { } s ? FileUtil.FormatSize(s) : "";

    [ObservableProperty] public partial bool IsInstalled { get; set; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => selectionChanged?.Invoke();
}
