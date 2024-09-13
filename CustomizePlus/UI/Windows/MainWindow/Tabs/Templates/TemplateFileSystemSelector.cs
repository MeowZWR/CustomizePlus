﻿using Dalamud.Interface;
using Dalamud.Plugin.Services;
using ImGuiNET;
using OtterGui;
using OtterGui.Classes;
using OtterGui.Filesystem;
using OtterGui.FileSystem.Selector;
using OtterGui.Log;
using OtterGui.Raii;
using System;
using System.Numerics;
using static CustomizePlus.UI.Windows.MainWindow.Tabs.Templates.TemplateFileSystemSelector;
using Newtonsoft.Json;
using System.Linq;
using Dalamud.Interface.ImGuiFileDialog;
using System.IO;
using CustomizePlus.Templates;
using CustomizePlus.Configuration.Data;
using CustomizePlus.Profiles;
using CustomizePlus.Core.Helpers;
using CustomizePlus.Anamnesis;
using CustomizePlus.Profiles.Data;
using CustomizePlus.Templates.Events;
using CustomizePlus.Profiles.Events;
using CustomizePlus.Templates.Data;
using CustomizePlus.Configuration.Helpers;
using CustomizePlus.Configuration.Data.Version3;
using CustomizePlus.Configuration.Data.Version2;
using Dalamud.Interface.ImGuiNotification;

namespace CustomizePlus.UI.Windows.MainWindow.Tabs.Templates;

public class TemplateFileSystemSelector : FileSystemSelector<Template, TemplateState>
{
    private readonly PluginConfiguration _configuration;
    private readonly TemplateEditorManager _editorManager;
    private readonly TemplateManager _templateManager;
    private readonly TemplateChanged _templateChangedEvent;
    private readonly ProfileChanged _profileChangedEvent;
    private readonly ProfileManager _profileManager;
    private readonly MessageService _messageService;
    private readonly PoseFileBoneLoader _poseFileBoneLoader;
    private readonly Logger _logger;
    private readonly PopupSystem _popupSystem;

    private readonly FileDialogManager _importFilePicker = new();

    private string? _clipboardText;
    private Template? _cloneTemplate;
    private string _newName = string.Empty;

    public bool IncognitoMode
    {
        get => _configuration.UISettings.IncognitoMode;
        set
        {
            _configuration.UISettings.IncognitoMode = value;
            _configuration.Save();
        }
    }

    public struct TemplateState
    {
        public ColorId Color;
    }

    public TemplateFileSystemSelector(
        TemplateFileSystem fileSystem,
        IKeyState keyState,
        Logger logger,
        PluginConfiguration configuration,
        TemplateEditorManager editorManager,
        TemplateManager templateManager,
        TemplateChanged templateChangedEvent,
        ProfileChanged profileChangedEvent,
        ProfileManager profileManager,
        MessageService messageService,
        PoseFileBoneLoader poseFileBoneLoader,
        PopupSystem popupSystem)
        : base(fileSystem, keyState, logger, allowMultipleSelection: true)
    {
        _configuration = configuration;
        _editorManager = editorManager;
        _templateManager = templateManager;
        _templateChangedEvent = templateChangedEvent;
        _profileChangedEvent = profileChangedEvent;
        _profileManager = profileManager;
        _messageService = messageService;
        _poseFileBoneLoader = poseFileBoneLoader;
        _logger = logger;
        _popupSystem = popupSystem;


        _templateChangedEvent.Subscribe(OnTemplateChange, TemplateChanged.Priority.TemplateFileSystemSelector);
        _profileChangedEvent.Subscribe(OnProfileChange, ProfileChanged.Priority.TemplateFileSystemSelector);

        AddButton(NewButton, 0);
        AddButton(AnamnesisImportButton, 10);
        AddButton(ClipboardImportButton, 20);
        AddButton(CloneButton, 30);
        AddButton(DeleteButton, 1000);
        SetFilterTooltip();
    }

    public void Dispose()
    {
        base.Dispose();
        _templateChangedEvent.Unsubscribe(OnTemplateChange);
        _profileChangedEvent.Unsubscribe(OnProfileChange);
    }

    protected override uint ExpandedFolderColor
        => ColorId.FolderExpanded.Value();

    protected override uint CollapsedFolderColor
        => ColorId.FolderCollapsed.Value();

    protected override uint FolderLineColor
        => ColorId.FolderLine.Value();

    protected override bool FoldersDefaultOpen
        => _configuration.UISettings.FoldersDefaultOpen;

    protected override void DrawLeafName(FileSystem<Template>.Leaf leaf, in TemplateState state, bool selected)
    {
        var flag = selected ? ImGuiTreeNodeFlags.Selected | LeafFlags : LeafFlags;
        var name = IncognitoMode ? leaf.Value.Incognito : leaf.Value.Name.Text;
        using var color = ImRaii.PushColor(ImGuiCol.Text, state.Color.Value());
        using var _ = ImRaii.TreeNode(name, flag);
    }

    protected override void Select(FileSystem<Template>.Leaf? leaf, bool clear, in TemplateState storage = default)
    {
        if (_editorManager.IsEditorActive)
        {
            Plugin.Logger.Debug("Blocked edited item change");
            ShowEditorWarningPopup();
            return;
        }

        base.Select(leaf, clear, storage);
    }

    protected override void DrawPopups()
    {
        _importFilePicker.Draw();

        //DrawEditorWarningPopup();
        DrawNewTemplatePopup();
    }

    private void ShowEditorWarningPopup()
    {
        _popupSystem.ShowPopup(PopupSystem.Messages.TemplateEditorActiveWarning);
    }

    private void DrawNewTemplatePopup()
    {
        if (!ImGuiUtil.OpenNameField("##NewTemplate", ref _newName))
            return;

        try
        {
            if (_clipboardText != null)
            {
                var importVer = Base64Helper.ImportFromBase64(_clipboardText, out var json);

                var template = Convert.ToInt32(importVer) switch
                {
                    2 => GetTemplateFromV2Profile(json),
                    3 => GetTemplateFromV3Profile(json),
                    4 => JsonConvert.DeserializeObject<Template>(json),
                    _ => null
                };
                if (template is Template tpl && tpl != null)
                    _templateManager.Clone(tpl, _newName, true);
                else
                    _popupSystem.ShowPopup(PopupSystem.Messages.ClipboardDataUnsupported);
            }
            else if (_cloneTemplate != null)
            {
                _templateManager.Clone(_cloneTemplate, _newName, true);
            }
            else
            {
                _templateManager.Create(_newName, true);
            }
        }
        catch(Exception ex)
        {
            _logger.Error($"Error while performing clipboard/clone/create template action: {ex}");
            _popupSystem.ShowPopup(PopupSystem.Messages.ActionError);
        }
        finally
        {
            _clipboardText = null;
            _cloneTemplate = null;
            _newName = string.Empty;
        }
    }

    private Template? GetTemplateFromV2Profile(string json)
    {
        var profile = JsonConvert.DeserializeObject<Version2Profile>(json);
        if (profile != null)
        {
            var v3Profile = V2ProfileToV3Converter.Convert(profile);

            (var _, var template) = V3ProfileToV4Converter.Convert(v3Profile);

            if (template != null)
                return template;
        }

        return null;
    }

    private Template? GetTemplateFromV3Profile(string json)
    {
        var profile = JsonConvert.DeserializeObject<Version3Profile>(json);
        if (profile != null)
        {
            if (profile.ConfigVersion != 3)
                throw new Exception("Incompatible profile version");

            (var _, var template) = V3ProfileToV4Converter.Convert(profile);

            if (template != null)
                return template;
        }

        return null;
    }

    private void OnTemplateChange(TemplateChanged.Type type, Template? nullable, object? arg3 = null)
    {
        switch (type)
        {
            case TemplateChanged.Type.Created:
            case TemplateChanged.Type.Deleted:
            case TemplateChanged.Type.Renamed:
            case TemplateChanged.Type.ReloadedAll:
                SetFilterDirty();
                break;
        }
    }

    private void OnProfileChange(ProfileChanged.Type type, Profile? profile, object? arg3 = null)
    {
        switch (type)
        {
            case ProfileChanged.Type.Created:
            case ProfileChanged.Type.Deleted:
            case ProfileChanged.Type.AddedTemplate:
            case ProfileChanged.Type.ChangedTemplate:
            case ProfileChanged.Type.RemovedTemplate:
            case ProfileChanged.Type.ReloadedAll:
                SetFilterDirty();
                break;
        }
    }

    private void NewButton(Vector2 size)
    {
        if (!ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Plus.ToIconString(), size, "创建一个包含默认设置的模板文件。", false,
                true))
            return;

        if (_editorManager.IsEditorActive)
        {
            ShowEditorWarningPopup();
            return;
        }

        ImGui.OpenPopup("##NewTemplate");
    }

    private void ClipboardImportButton(Vector2 size)
    {
        if (!ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Clipboard.ToIconString(), size, "尝试从剪贴板导入模板。", false,
                true))
            return;

        if (_editorManager.IsEditorActive)
        {
            ShowEditorWarningPopup();
            return;
        }

        try
        {
            _clipboardText = ImGui.GetClipboardText();
            ImGui.OpenPopup("##NewTemplate");
        }
        catch
        {
            _messageService.NotificationMessage("无法从剪贴板导入数据。", NotificationType.Error, false);
        }
    }

    private void AnamnesisImportButton(Vector2 size)
    {
        if (!ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.FileImport.ToIconString(), size, "从Anamnesis的pose文件导入模板（仅限缩放）", false,
                true))
            return;

        if (_editorManager.IsEditorActive)
        {
            ShowEditorWarningPopup();
            return;
        }

        _importFilePicker.OpenFileDialog("导入Pose文件", ".pose", (isSuccess, path) =>
        {
            if (isSuccess)
            {
                var selectedFilePath = path.FirstOrDefault();
                //todo: check for selectedFilePath == null?

                var bones = _poseFileBoneLoader.LoadBoneTransformsFromFile(selectedFilePath);

                if (bones != null)
                {
                    if (bones.Count == 0)
                    {
                        _messageService.NotificationMessage("选择的Anamnesis pose文件不包含任何的骨骼缩放数据。", NotificationType.Error);
                        return;
                    }

                    _templateManager.Create(Path.GetFileNameWithoutExtension(selectedFilePath), bones, false);
                }
                else
                {
                    _messageService.NotificationMessage(
                        $"在分析位于'{path}'的Anamnesis pose文件时出错", NotificationType.Error);
                }
            }
            else
            {
                _logger.Debug(isSuccess + "没有选择有效的文件。" + path);
            }
        }, 1, null, true);

        /*MessageDialog.Show(
            "Due to technical limitations, Customize+ is only able to import scale values from *.pose files.\nPosition and rotation information will be ignored.",
            new Vector2(570, 100), ImportAction, "ana_import_pos_rot_warning");*/
        //todo: message dialog?
    }

    private void CloneButton(Vector2 size)
    {
        var tt = SelectedLeaf == null
            ? "没有模板被选中。"
            : "为当前选中的模板克隆一个副本";
        if (!ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Clone.ToIconString(), size, tt, SelectedLeaf == null, true))
            return;

        if (_editorManager.IsEditorActive)
        {
            ShowEditorWarningPopup();
            return;
        }

        _cloneTemplate = Selected!;
        ImGui.OpenPopup("##NewTemplate");
    }

    private void DeleteButton(Vector2 size)
        => DeleteSelectionButton(size, _configuration.UISettings.DeleteTemplateModifier, "template", "templates", (template) =>
        {
            if (_editorManager.IsEditorActive)
            {
                ShowEditorWarningPopup();
                return;
            }

            _templateManager.Delete(template);
        });

    #region Filters

    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;
    private LowerString _filter = LowerString.Empty;
    private int _filterType = -1;

    private void SetFilterTooltip()
    {
        FilterTooltip = "筛选在完整路径或名称中包含指定字符串的模板。\n"
          + "输入n:[字符串]只筛选模板名称，不筛选路径。";
    }

    /// <summary> Appropriately identify and set the string filter and its type. </summary>
    protected override bool ChangeFilter(string filterValue)
    {
        (_filter, _filterType) = filterValue.Length switch
        {
            0 => (LowerString.Empty, -1),
            > 1 when filterValue[1] == ':' =>
                filterValue[0] switch
                {
                    'n' => filterValue.Length == 2 ? (LowerString.Empty, -1) : (new LowerString(filterValue[2..]), 1),
                    'N' => filterValue.Length == 2 ? (LowerString.Empty, -1) : (new LowerString(filterValue[2..]), 1),
                    _ => (new LowerString(filterValue), 0),
                },
            _ => (new LowerString(filterValue), 0),
        };

        return true;
    }

    /// <summary>
    /// The overwritten filter method also computes the state.
    /// Folders have default state and are filtered out on the direct string instead of the other options.
    /// If any filter is set, they should be hidden by default unless their children are visible,
    /// or they contain the path search string.
    /// </summary>
    protected override bool ApplyFiltersAndState(FileSystem<Template>.IPath path, out TemplateState state)
    {
        if (path is TemplateFileSystem.Folder f)
        {
            state = default;
            return FilterValue.Length > 0 && !f.FullName().Contains(FilterValue, IgnoreCase);
        }

        return ApplyFiltersAndState((TemplateFileSystem.Leaf)path, out state);
    }

    /// <summary> Apply the string filters. </summary>
    private bool ApplyStringFilters(TemplateFileSystem.Leaf leaf, Template template)
    {
        return _filterType switch
        {
            -1 => false,
            0 => !(_filter.IsContained(leaf.FullName()) || template.Name.Contains(_filter)),
            1 => !template.Name.Contains(_filter),
            _ => false, // Should never happen
        };
    }

    /// <summary> Combined wrapper for handling all filters and setting state. </summary>
    private bool ApplyFiltersAndState(TemplateFileSystem.Leaf leaf, out TemplateState state)
    {
        //todo: more efficient to store links to profiles in templates than iterating here
        state.Color = _profileManager.GetProfilesUsingTemplate(leaf.Value).Any() ? ColorId.UsedTemplate : ColorId.UnusedTemplate;
        return ApplyStringFilters(leaf, leaf.Value);
    }

    #endregion
}
