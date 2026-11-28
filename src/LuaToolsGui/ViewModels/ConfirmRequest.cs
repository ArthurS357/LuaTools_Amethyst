namespace LuaToolsGui.ViewModels;

/// <summary>
/// One dark confirmation (rendered by <see cref="Views.RemoveGameDialog"/>). Cancel is always the default
/// button and the initial focus; <paramref name="IsDanger"/> paints the confirm button red.
/// </summary>
public sealed record ConfirmRequest(
    string Title,
    string Body,
    string ConfirmText,
    bool IsDanger = true,
    string? Heading = null,
    string? Caption = null,
    bool ShowVaultNotice = false);
