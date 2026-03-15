using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.UI;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Nexus;

namespace TerrariaModder.Core.UI
{
    internal sealed class NexusBrowseState : NativeModsStateBase
    {
        private static readonly FeedOption[] FeedOptions =
        {
            new FeedOption("all", "All"),
            new FeedOption("latest", "Latest"),
            new FeedOption("trending", "Trending"),
            new FeedOption("updated", "Updated"),
            new FeedOption("installed", "Installed")
        };

        private string _feed = "all";
        private readonly List<NexusMod> _mods = new List<NexusMod>();
        private readonly List<InstalledModRecord> _installed = new List<InstalledModRecord>();
        private readonly Dictionary<int, object> _browseTextures = new Dictionary<int, object>();
        private readonly Dictionary<int, string> _pendingImagePaths = new Dictionary<int, string>();
        private readonly HashSet<int> _requestedImages = new HashSet<int>();
        private bool _isLoading;
        private string _status = "Loading Nexus data...";
        private string _editingSearch;
        private string _searchBuffer = string.Empty;
        private BrowseRefreshResult _pendingRefresh;
        private int _pageIndex;
        private const int ItemsPerPage = 12;
        private const int GridColumns = 3;

        public NexusBrowseState(NativeModsService service, UIState previousState, bool inGame)
            : base(service, previousState, inGame)
        {
        }

        public override void OnInitialize()
        {
            base.OnInitialize();
            _ = RefreshAsync();
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            ApplyPendingRefresh();
            ApplyPendingImages();
            if (!string.IsNullOrEmpty(_editingSearch))
                UpdateSearchEditing();
        }

        protected override bool CanGoBack()
        {
            return string.IsNullOrEmpty(_editingSearch);
        }

        protected override string GetTitle() => "Browse Nexus";

        protected override void RebuildList()
        {
            List.Clear();
            List.Add(CreateInfoRow(_status, 42f));

            if (!Service.NexusAuth.HasApiKey)
            {
                List.Add(CreateInfoRow("You need a Nexus API key or browser login before browsing.", 44f));
                List.Add(CreateButton("Open Nexus Settings", () => OpenState(new NexusSettingsState(Service, this, InGame)), 42f));
                return;
            }

            List.Add(CreateSectionRow("Feed"));
            List.Add(CreateFeedButtonsRow());
            List.Add(CreateButton("Refresh", () => _ = RefreshAsync(), 40f));

            string searchLabel = string.IsNullOrEmpty(_editingSearch)
                ? ("Search: " + (string.IsNullOrWhiteSpace(_searchBuffer) ? "(click to enter name, URL, or mod id)" : _searchBuffer))
                : ("Search: " + _searchBuffer + "_");
            List.Add(CreateButton(searchLabel, BeginSearchEditing, 42f));

            List.Add(CreateSectionRow(_feed == "installed" ? "Installed Mods" : "Browse Results"));
            if (_isLoading)
            {
                List.Add(CreateInfoRow("Loading...", 38f));
                return;
            }

            if (_feed == "installed")
            {
                foreach (var mod in GetInstalledPageItems())
                    List.Add(CreateInstalledRow(mod));
            }
            else
            {
                foreach (var row in CreateBrowseGridRows(GetBrowsePageItems().ToList()))
                    List.Add(row);
            }

            if ((_feed == "installed" && _installed.Count == 0) || (_feed != "installed" && _mods.Count == 0))
                List.Add(CreateInfoRow("No mods found.", 38f));

            int totalItems = _feed == "installed" ? _installed.Count : _mods.Count;
            int pageCount = Math.Max(1, (int)Math.Ceiling(totalItems / (double)ItemsPerPage));
            if (pageCount > 1)
            {
                List.Add(CreateInfoRow("Page " + (_pageIndex + 1) + " of " + pageCount, 34f));
                List.Add(CreatePaginationRow(pageCount));
            }
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            _isLoading = true;
            _status = "Loading " + _feed + " feed...";
            _pendingRefresh = null;
            _pageIndex = 0;
            RebuildList();

            try
            {
                var result = new BrowseRefreshResult();
                if (_feed == "installed")
                {
                    result.InstalledMods.AddRange((await Service.GetInstalledModsAsync(includeUpdates: true).ConfigureAwait(false))
                        .Where(m => m.NexusModId > 0 || (m.Manifest != null && m.Manifest.NexusId > 0)));
                    result.Status = "Installed Nexus-linked mods: " + result.InstalledMods.Count;
                }
                else if (!string.IsNullOrWhiteSpace(_searchBuffer))
                {
                    result.Mods.AddRange(await Service.SearchNexusModsAsync(_searchBuffer).ConfigureAwait(false));
                    result.Status = "Search results: " + result.Mods.Count;
                }
                else
                {
                    result.Mods.AddRange(await Service.BrowseNexusModsAsync(_feed).ConfigureAwait(false));
                    result.Status = "Loaded " + result.Mods.Count + " mods from Nexus.";
                }

                _pendingRefresh = result;
            }
            catch (Exception ex)
            {
                _pendingRefresh = new BrowseRefreshResult
                {
                    Status = "Nexus load failed: " + ex.Message
                };
            }
        }

        private void ApplyPendingRefresh()
        {
            if (_pendingRefresh == null)
                return;

            _mods.Clear();
            _mods.AddRange(_pendingRefresh.Mods);
            _installed.Clear();
            _installed.AddRange(_pendingRefresh.InstalledMods);
            _status = _pendingRefresh.Status ?? _status;
            _isLoading = false;
            _pendingRefresh = null;
            RebuildList();
        }

        private void BeginSearchEditing()
        {
            _editingSearch = "search";
            Main.clrInput();
            RebuildList();
        }

        private void UpdateSearchEditing()
        {
            PlayerInput.WritingText = true;
            Main.instance.HandleIME();
            string newText = Main.GetInputText(_searchBuffer ?? string.Empty);
            if (!string.Equals(newText, _searchBuffer, StringComparison.Ordinal))
            {
                _searchBuffer = newText;
                RebuildList();
            }

            if (InputState.IsKeyJustPressed(KeyCode.Escape))
            {
                _editingSearch = null;
                Main.clrInput();
                PlayerInput.WritingText = false;
                RebuildList();
                return;
            }

            if (!Main.inputTextEnter)
                return;

            _editingSearch = null;
            Main.clrInput();
            PlayerInput.WritingText = false;
            _ = RefreshAsync();
        }

        private UIElement CreateSectionRow(string title)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(44f, 0f);
            panel.BackgroundColor = new Color(45, 61, 108) * 0.95f;
            panel.BorderColor = new Color(100, 123, 184);

            var text = new UIText(title, 0.68f, large: true);
            text.HAlign = 0.5f;
            text.VAlign = 0.5f;
            text.Top.Set(-4f, 0f);
            panel.Append(text);
            return panel;
        }

        private UIElement CreateFeedButtonsRow()
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(60f, 0f);
            panel.SetPadding(0f);
            panel.BackgroundColor = new Color(29, 38, 66) * 0.96f;
            panel.BorderColor = new Color(68, 86, 140);

            const float gap = 10f;
            const float buttonWidth = 150f;
            const float buttonHeight = 38f;
            float totalWidth = (FeedOptions.Length * buttonWidth) + ((FeedOptions.Length - 1) * gap);
            float startLeft = -totalWidth * 0.5f;

            for (int index = 0; index < FeedOptions.Length; index++)
            {
                FeedOption option = FeedOptions[index];
                bool active = string.Equals(_feed, option.Key, StringComparison.Ordinal);
                var button = new UITextPanel<string>(option.Label, 0.58f, large: false);
                button.Width.Set(buttonWidth, 0f);
                button.Height.Set(buttonHeight, 0f);
                button.Left.Set(startLeft + (index * (buttonWidth + gap)), 0.5f);
                button.Top.Set(11f, 0f);
                button.BackgroundColor = active ? new Color(82, 108, 190) * 0.98f : new Color(52, 71, 121) * 0.95f;
                button.BorderColor = active ? Color.White : new Color(126, 147, 208);
                button.OnMouseOver += FadedMouseOver;
                button.OnMouseOut += (_, __) =>
                {
                    button.BackgroundColor = active ? new Color(82, 108, 190) * 0.98f : new Color(52, 71, 121) * 0.95f;
                    button.BorderColor = active ? Color.White : new Color(126, 147, 208);
                };
                button.OnLeftClick += (evt, element) =>
                {
                    if (string.Equals(_feed, option.Key, StringComparison.Ordinal))
                        return;

                    _feed = option.Key;
                    if (_feed != "installed")
                        _searchBuffer = string.Empty;
                    _pageIndex = 0;
                    _ = RefreshAsync();
                };
                panel.Append(button);
            }

            return panel;
        }

        private List<UIElement> CreateBrowseGridRows(List<NexusMod> mods)
        {
            var rows = new List<UIElement>();
            if (mods == null || mods.Count == 0)
                return rows;

            for (int index = 0; index < mods.Count; index += GridColumns)
            {
                rows.Add(CreateBrowseGridRow(mods.Skip(index).Take(GridColumns).ToList()));
            }

            return rows;
        }

        private IEnumerable<NexusMod> GetBrowsePageItems()
        {
            return _mods.Skip(_pageIndex * ItemsPerPage).Take(ItemsPerPage);
        }

        private IEnumerable<InstalledModRecord> GetInstalledPageItems()
        {
            return _installed.Skip(_pageIndex * ItemsPerPage).Take(ItemsPerPage);
        }

        private UIElement CreatePaginationRow(int pageCount)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(54f, 0f);
            panel.SetPadding(0f);
            panel.BackgroundColor = new Color(29, 38, 66) * 0.96f;
            panel.BorderColor = new Color(68, 86, 140);

            var previous = new UITextPanel<string>("Previous", 0.62f, large: false);
            previous.Width.Set(128f, 0f);
            previous.Height.Set(36f, 0f);
            previous.Top.Set(9f, 0f);
            previous.BackgroundColor = _pageIndex > 0 ? new Color(52, 71, 121) * 0.95f : new Color(35, 42, 63) * 0.95f;
            previous.BorderColor = new Color(126, 147, 208);
            previous.OnMouseOver += FadedMouseOver;
            previous.OnMouseOut += FadedMouseOut;
            previous.OnLeftClick += (_, __) =>
            {
                if (_pageIndex <= 0) return;
                _pageIndex--;
                RebuildList();
            };
            panel.Append(previous);

            var next = new UITextPanel<string>("Next", 0.62f, large: false);
            next.Width.Set(128f, 0f);
            next.Height.Set(36f, 0f);
            next.Top.Set(9f, 0f);
            next.BackgroundColor = _pageIndex < pageCount - 1 ? new Color(52, 71, 121) * 0.95f : new Color(35, 42, 63) * 0.95f;
            next.BorderColor = new Color(126, 147, 208);
            next.OnMouseOver += FadedMouseOver;
            next.OnMouseOut += FadedMouseOut;
            next.OnLeftClick += (_, __) =>
            {
                if (_pageIndex >= pageCount - 1) return;
                _pageIndex++;
                RebuildList();
            };
            panel.Append(next);

            var label = new UIText("Browse more filtered results", 0.58f, large: false);
            label.HAlign = 0.5f;
            label.VAlign = 0.5f;
            label.Top.Set(-11f, 0f);
            panel.Append(label);

            float centerOffset = 160f;
            previous.Left.Set(-centerOffset - 64f, 0.5f);
            next.Left.Set(centerOffset - 64f, 0.5f);

            return panel;
        }

        private UIElement CreateBrowseGridRow(List<NexusMod> mods)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(246f, 0f);
            panel.SetPadding(0f);
            panel.BackgroundColor = new Color(23, 31, 55) * 0.88f;
            panel.BorderColor = new Color(65, 83, 139);
            const float tileGap = 14f;
            const float tileWidth = 252f;
            const float tileHeight = 226f;
            float totalWidth = (mods.Count * tileWidth) + ((mods.Count - 1) * tileGap);
            float startLeft = -totalWidth * 0.5f;

            for (int index = 0; index < mods.Count; index++)
            {
                var tile = CreateBrowseTile(mods[index]);
                tile.Width.Set(tileWidth, 0f);
                tile.Height.Set(tileHeight, 0f);
                tile.Left.Set(startLeft + (index * (tileWidth + tileGap)), 0.5f);
                tile.Top.Set(10f, 0f);
                panel.Append(tile);
            }

            return panel;
        }

        private UIElement CreateBrowseTile(NexusMod mod)
        {
            var panel = new UIPanel();
            panel.SetPadding(0f);
            panel.BackgroundColor = new Color(29, 38, 66) * 0.98f;
            panel.BorderColor = new Color(68, 86, 140);
            panel.OnMouseOver += FadedMouseOver;
            panel.OnMouseOut += (_, __) =>
            {
                panel.BackgroundColor = new Color(29, 38, 66) * 0.98f;
                panel.BorderColor = new Color(68, 86, 140);
            };
            panel.OnLeftClick += (_, __) => OpenState(new NexusModDetailState(Service, this, InGame, mod.ModId, mod));

            var imagePanel = new UIPanel();
            imagePanel.Left.Set(12f, 0f);
            imagePanel.Top.Set(12f, 0f);
            imagePanel.Width.Set(-24f, 1f);
            imagePanel.Height.Set(118f, 0f);
            imagePanel.SetPadding(0f);
            imagePanel.BackgroundColor = new Color(19, 26, 45) * 0.98f;
            imagePanel.BorderColor = new Color(79, 99, 157);
            panel.Append(imagePanel);

            object texture = GetBrowseTexture(mod);
            if (texture != null)
            {
                var image = new BrowseIconElement(texture);
                image.Width.Set(-12f, 1f);
                image.Height.Set(-12f, 1f);
                image.HAlign = 0.5f;
                image.VAlign = 0.5f;
                imagePanel.Append(image);
            }
            else
            {
                var placeholder = new UIText("NEXUS", 0.82f, large: true);
                placeholder.HAlign = 0.5f;
                placeholder.VAlign = 0.5f;
                imagePanel.Append(placeholder);
                RequestBrowseImage(mod);
            }

            var name = new UIText(mod.Name ?? ("Mod " + mod.ModId), 0.64f, large: false);
            name.Left.Set(12f, 0f);
            name.Top.Set(138f, 0f);
            name.Width.Set(-24f, 1f);
            name.Height.Set(50f, 0f);
            name.IsWrapped = true;
            panel.Append(name);

            string status = !mod.IsInstalled ? "Not installed"
                : mod.IsPendingDelete ? (mod.PendingDeleteIncludesSettings ? "Pending deletion + settings" : "Pending deletion")
                : mod.HasNewerVersion ? "Installed - update available"
                : "Installed v" + mod.InstalledVersion;
            var detail = new UIText(status, 0.5f, large: false);
            detail.Left.Set(12f, 0f);
            detail.Top.Set(192f, 0f);
            detail.Width.Set(-24f, 1f);
            detail.Height.Set(20f, 0f);
            detail.HAlign = 0f;
            detail.TextColor = mod.HasNewerVersion ? new Color(242, 188, 84) : new Color(186, 197, 228);
            panel.Append(detail);

            return panel;
        }

        private UIElement CreateInstalledRow(InstalledModRecord mod)
        {
            string action = mod.NexusModId > 0 ? "Details" : "Info";
            string subtitle = mod.HasUpdate ? ("Update available: " + mod.LatestVersion) : ("Installed v" + mod.Version);
            return CreateActionRow(mod.Name, subtitle, action, () =>
            {
                if (mod.NexusModId > 0)
                    OpenState(new NexusModDetailState(Service, this, InGame, mod.NexusModId, null));
            });
        }

        private object GetBrowseTexture(NexusMod mod)
        {
            if (mod == null)
                return null;

            if (_browseTextures.TryGetValue(mod.ModId, out object texture))
                return texture;

            string cachedPath = Service.GetCachedNexusImagePath(mod.ModId);
            if (string.IsNullOrWhiteSpace(cachedPath))
                return null;

            texture = UIRenderer.LoadTexture(cachedPath);
            if (texture != null)
                _browseTextures[mod.ModId] = texture;

            return texture;
        }

        private void RequestBrowseImage(NexusMod mod)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.PictureUrl) || _requestedImages.Contains(mod.ModId))
                return;

            _requestedImages.Add(mod.ModId);
            _ = FetchBrowseImageAsync(mod);
        }

        private async System.Threading.Tasks.Task FetchBrowseImageAsync(NexusMod mod)
        {
            string path = await Service.EnsureNexusImageCachedAsync(mod).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
                _pendingImagePaths[mod.ModId] = path;
        }

        private void ApplyPendingImages()
        {
            if (_pendingImagePaths.Count == 0)
                return;

            bool changed = false;
            foreach (var pair in _pendingImagePaths.ToList())
            {
                object texture = UIRenderer.LoadTexture(pair.Value);
                if (texture == null)
                    continue;

                _browseTextures[pair.Key] = texture;
                changed = true;
                _pendingImagePaths.Remove(pair.Key);
            }

            if (changed)
                RebuildList();
        }

        private UIElement CreateActionRow(string title, string subtitle, string buttonLabel, Action onClick)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(70f, 0f);
            panel.BackgroundColor = new Color(29, 38, 66) * 0.96f;
            panel.BorderColor = new Color(68, 86, 140);

            var heading = new UIText(title, 0.74f, large: false);
            heading.Left.Set(14f, 0f);
            heading.Top.Set(10f, 0f);
            panel.Append(heading);

            var detail = new UIText(subtitle, 0.56f, large: false);
            detail.Left.Set(14f, 0f);
            detail.Top.Set(40f, 0f);
            detail.TextColor = new Color(186, 197, 228);
            panel.Append(detail);

            var button = new UITextPanel<string>(buttonLabel, 0.62f, large: false);
            button.Width.Set(102f, 0f);
            button.Height.Set(38f, 0f);
            button.Left.Set(-116f, 1f);
            button.VAlign = 0.5f;
            button.BackgroundColor = new Color(52, 71, 121) * 0.95f;
            button.BorderColor = new Color(126, 147, 208);
            button.OnMouseOver += FadedMouseOver;
            button.OnMouseOut += FadedMouseOut;
            button.OnLeftClick += (_, __) => onClick();
            panel.Append(button);

            return panel;
        }

        private sealed class BrowseRefreshResult
        {
            public List<NexusMod> Mods { get; } = new List<NexusMod>();
            public List<InstalledModRecord> InstalledMods { get; } = new List<InstalledModRecord>();
            public string Status { get; set; }
        }

        private sealed class FeedOption
        {
            public FeedOption(string key, string label)
            {
                Key = key;
                Label = label;
            }

            public string Key { get; }
            public string Label { get; }
        }

        private sealed class BrowseIconElement : UIElement
        {
            private readonly object _textureSource;

            public BrowseIconElement(object textureSource)
            {
                _textureSource = textureSource;
            }

            protected override void DrawSelf(SpriteBatch spriteBatch)
            {
                base.DrawSelf(spriteBatch);

                Texture2D texture = ResolveTexture(_textureSource);
                if (texture == null)
                    return;

                CalculatedStyle dimensions = GetInnerDimensions();
                float scale = Math.Min(dimensions.Width / texture.Width, dimensions.Height / texture.Height);
                int drawWidth = Math.Max(1, (int)(texture.Width * scale));
                int drawHeight = Math.Max(1, (int)(texture.Height * scale));
                var destination = new Rectangle(
                    (int)(dimensions.X + ((dimensions.Width - drawWidth) * 0.5f)),
                    (int)(dimensions.Y + ((dimensions.Height - drawHeight) * 0.5f)),
                    drawWidth,
                    drawHeight);

                spriteBatch.Draw(texture, destination, Color.White);
            }

            private static Texture2D ResolveTexture(object textureSource)
            {
                if (textureSource is Texture2D texture)
                    return texture;

                var sourceType = textureSource?.GetType();
                var valueProperty = sourceType?.GetProperty("Value");
                return valueProperty?.GetValue(textureSource, null) as Texture2D;
            }
        }
    }

    internal sealed class NexusModDetailState : NativeModsStateBase
    {
        private NexusMod _mod;
        private readonly int _modId;
        private List<NexusModFile> _files = new List<NexusModFile>();
        private InstalledModRecord _installed;
        private bool _loading;
        private string _status = "Loading Nexus details...";
        private DetailRefreshResult _pendingRefresh;
        private UIElement _summaryArea;
        private UIPanel _iconPanel;
        private UIPanel _metaPanel;
        private object _iconTexture;
        private string _pendingImagePath;

        public NexusModDetailState(NativeModsService service, UIState previousState, bool inGame, int modId, NexusMod existingMod)
            : base(service, previousState, inGame)
        {
            _modId = modId;
            _mod = existingMod;
        }

        public override void OnInitialize()
        {
            base.OnInitialize();
            BuildDetailLayout();
            _ = RefreshAsync();
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            ApplyPendingImage();
            ApplyPendingRefresh();
        }

        protected override string GetTitle() => "Nexus Mod Details";

        protected override void RebuildList()
        {
            float scrollPosition = GetScrollPosition();
            List.Clear();

            if (_mod == null)
            {
                if (!string.IsNullOrWhiteSpace(_status))
                    List.Add(CreateInfoRow(_status, 42f));
                List.Add(CreateInfoRow("No mod details available.", 40f));
                return;
            }

            if (_loading)
                List.Add(CreateInfoRow("Loading description and file metadata...", 36f));
            else if (!string.IsNullOrWhiteSpace(_status) &&
                     (_status.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      _status.IndexOf("install", StringComparison.OrdinalIgnoreCase) >= 0))
                List.Add(CreateInfoRow(_status, 40f));

            List.Add(CreateSectionRow("Description"));
            string description = !string.IsNullOrWhiteSpace(_mod.Description) ? _mod.Description : (_mod.Summary ?? "No description provided.");
            var descriptionBlocks = NormalizeDescriptionBlocks(description);
            List.Add(CreateDescriptionPanel(descriptionBlocks));

            var links = descriptionBlocks.Where(b => b.Kind == DescriptionBlockKind.Link).ToList();
            if (links.Count > 0)
            {
                List.Add(CreateSectionRow("Links"));
                foreach (var link in links)
                    List.Add(CreateLinkRow(link.Text, link.Url));
            }

            List.Add(CreateSectionRow("Files"));
            if (_files.Count == 0)
            {
                List.Add(CreateInfoRow("No files returned from Nexus.", 36f));
            }
            else
            {
                foreach (var file in _files.Take(12))
                    List.Add(CreateInfoRow(FormatFile(file), 42f));
            }

            RestoreScrollPosition(scrollPosition);
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            _loading = true;
            _pendingRefresh = null;
            RebuildList();

            try
            {
                var result = new DetailRefreshResult();
                if (_mod == null || string.IsNullOrWhiteSpace(_mod.Description))
                    result.Mod = await Service.GetNexusModDetailAsync(_modId).ConfigureAwait(false);
                else
                    result.Mod = _mod;

                result.Files = await Service.GetNexusModFilesAsync(_modId).ConfigureAwait(false) ?? new List<NexusModFile>();
                var installed = await Service.GetInstalledModsAsync(includeUpdates: true).ConfigureAwait(false);
                result.Installed = Service.FindInstalledModMatch(result.Mod, installed);
                if (result.Mod != null && result.Installed != null)
                {
                    Service.LinkInstalledMod(result.Installed, _modId);
                    result.Mod.IsInstalled = true;
                    result.Mod.InstalledVersion = result.Installed.Version;
                    result.Mod.HasNewerVersion = result.Installed.HasUpdate || NexusUpdateTracker.IsNewerVersion(result.Mod.Version ?? string.Empty, result.Installed.Version ?? string.Empty);
                    result.Mod.IsPendingDelete = result.Installed.IsPendingDelete;
                    result.Mod.PendingDeleteIncludesSettings = result.Installed.PendingDeleteIncludesSettings;
                }

                result.Status = result.Mod != null ? null : "Failed to load Nexus details.";
                _pendingRefresh = result;
                if (result.Mod != null && !string.IsNullOrWhiteSpace(result.Mod.PictureUrl))
                    _ = FetchImageAsync(result.Mod);
            }
            catch (Exception ex)
            {
                _pendingRefresh = new DetailRefreshResult
                {
                    Status = "Failed to load details: " + ex.Message
                };
            }
        }

        private void HandleInstallClicked()
        {
            NexusModFile mainFile = GetMainFile();
            if (mainFile == null)
            {
                Service.OpenNexusPage(_modId, filesTab: true);
                return;
            }

            if (_mod.IsInstalled)
            {
                bool keepSettings = _installed != null && _installed.HasConfigFiles;
                string message = keepSettings
                    ? "This mod is already installed. Keep existing settings or do a clean reinstall?"
                    : "This mod is already installed. Reinstall from Nexus?";
                OpenState(new ConfirmDialogState(
                    Service,
                    this,
                    InGame,
                    "Install From Nexus",
                    message,
                    keepSettings ? "Keep Settings" : "Reinstall",
                    () => _ = InstallAsync(mainFile, keepSettings ? ConfigPreservationMode.Keep : ConfigPreservationMode.Delete),
                    keepSettings ? "Clean Install" : null,
                    keepSettings ? (Action)(() => _ = InstallAsync(mainFile, ConfigPreservationMode.Delete)) : null));
                return;
            }

            _ = InstallAsync(mainFile, ConfigPreservationMode.Keep);
        }

        private async System.Threading.Tasks.Task InstallAsync(NexusModFile file, ConfigPreservationMode mode)
        {
            _status = "Installing " + _mod.Name + "...";
            RebuildList();

            var result = await Service.DownloadAndInstallNexusModAsync(_modId, file.FileId, mode).ConfigureAwait(false);
            _status = result.Success ? "Install completed." : ("Install failed: " + (result.Error ?? "unknown error"));
            await RefreshAsync().ConfigureAwait(false);
        }

        private void ApplyPendingRefresh()
        {
            if (_pendingRefresh == null)
                return;

            if (_pendingRefresh.Mod != null)
                _mod = _pendingRefresh.Mod;
            _files = _pendingRefresh.Files ?? _files;
            _installed = _pendingRefresh.Installed;
            _status = _pendingRefresh.Status ?? _status;
            _loading = false;
            _pendingRefresh = null;
            RebuildHeaderContents();
            RebuildList();
        }

        private void BuildDetailLayout()
        {
            PluginLoader.LoadModIcons();

            Root.MaxWidth.Set(1040f, 0f);

            Panel.Top.Set(244f, 0f);
            Panel.Height.Set(-352f, 1f);
            Panel.BackgroundColor = new Color(22, 30, 52) * 0.92f;

            List.Top.Set(14f, 0f);
            List.Height.Set(-26f, 1f);
            List.ListPadding = 10f;

            _summaryArea = new UIElement();
            _summaryArea.Width.Set(0f, 1f);
            _summaryArea.Height.Set(248f, 0f);
            _summaryArea.Top.Set(-8f, 0f);
            Root.Append(_summaryArea);

            _iconPanel = new UIPanel();
            _iconPanel.Width.Set(188f, 0f);
            _iconPanel.Height.Set(248f, 0f);
            _iconPanel.BackgroundColor = new Color(28, 37, 66) * 0.95f;
            _summaryArea.Append(_iconPanel);

            _metaPanel = new UIPanel();
            _metaPanel.Left.Set(202f, 0f);
            _metaPanel.Width.Set(-202f, 1f);
            _metaPanel.Height.Set(248f, 0f);
            _metaPanel.BackgroundColor = new Color(28, 37, 66) * 0.95f;
            _summaryArea.Append(_metaPanel);

            RebuildHeaderContents();
        }

        private void RebuildHeaderContents()
        {
            if (_iconPanel == null || _metaPanel == null)
                return;

            _iconPanel.RemoveAllChildren();
            _metaPanel.RemoveAllChildren();

            BuildIconContents();
            BuildMetaContents();
            BringToFront(TitlePanel);
        }

        private void BuildIconContents()
        {
            object texture = PluginLoader.DefaultIcon;
            if (_iconTexture != null)
            {
                texture = _iconTexture;
            }
            else if (_installed != null)
            {
                var loadedMod = PluginLoader.GetMod(_installed.Id);
                if (loadedMod?.IconTexture != null)
                    texture = loadedMod.IconTexture;
            }

            if (texture != null)
            {
                var image = new NexusIconElement(texture);
                image.Width.Set(132f, 0f);
                image.Height.Set(132f, 0f);
                image.HAlign = 0.5f;
                image.VAlign = 0.42f;
                _iconPanel.Append(image);
            }
            else
            {
                var placeholder = new UIText("NEXUS\nMOD", 0.9f, large: true);
                placeholder.HAlign = 0.5f;
                placeholder.VAlign = 0.42f;
                _iconPanel.Append(placeholder);
            }

            string footerText = _mod != null && _mod.IsPendingDelete
                ? "Pending deletion"
                : _mod != null && _mod.IsInstalled ? "Installed" : "Nexus listing";
            var footer = new UIText(footerText, 0.6f, large: false);
            footer.HAlign = 0.5f;
            footer.Top.Set(206f, 0f);
            footer.TextColor = new Color(186, 197, 228);
            _iconPanel.Append(footer);
        }

        private void BuildMetaContents()
        {
            float top = 16f;
            float buttonTop = 126f;

            string title = _mod != null ? (_mod.Name ?? ("Mod " + _modId)) : ("Mod " + _modId);
            var name = new UIText(title, 0.96f, large: true);
            name.Left.Set(16f, 0f);
            name.Top.Set(top, 0f);
            _metaPanel.Append(name);
            top += 38f;

            _metaPanel.Append(CreateMetaText("Mod ID: " + _modId, top));
            top += 24f;
            _metaPanel.Append(CreateMetaText("Version: " + (_mod?.Version ?? "?"), top));
            top += 24f;
            _metaPanel.Append(CreateMetaText("Author: " + (_mod?.Author ?? "Unknown"), top));
            top += 24f;

            string state = !_loading
                ? (_mod != null && _mod.IsInstalled
                    ? (_mod.IsPendingDelete
                        ? (_mod.PendingDeleteIncludesSettings ? "Pending deletion + settings" : "Pending deletion")
                        : (_mod.HasNewerVersion ? "Installed - update available" : "Installed"))
                    : "Not installed")
                : "Loading...";
            var stateText = CreateMetaText("State: " + state, top);
            stateText.TextColor = _mod != null && _mod.IsPendingDelete
                ? new Color(242, 188, 84)
                : _mod != null && _mod.HasNewerVersion
                ? new Color(242, 188, 84)
                : new Color(206, 214, 236);
            _metaPanel.Append(stateText);

            int buttonWidth = 146;
            int buttonHeight = 34;
            int buttonGap = 10;

            if (_mod != null && _mod.IsPendingDelete)
            {
                var undoDelete = CreateActionButton(
                    "Undo Delete",
                    16f,
                    buttonTop,
                    buttonWidth,
                    buttonHeight,
                    HandleUndoDeleteClicked);
                _metaPanel.Append(undoDelete);
            }
            else
            {
                var primary = CreateActionButton(
                    _mod != null && _mod.IsInstalled ? (_mod.HasNewerVersion ? "Install Update" : "Reinstall") : "Install",
                    16f,
                    buttonTop,
                    buttonWidth,
                    buttonHeight,
                    HandleInstallClicked);
                _metaPanel.Append(primary);
            }

            var open = CreateActionButton(
                "Open On Nexus",
                16f + buttonWidth + buttonGap,
                buttonTop,
                buttonWidth,
                buttonHeight,
                () => Service.OpenNexusPage(_modId));
            _metaPanel.Append(open);

            if (_mod != null && _mod.IsInstalled && _installed != null && !_mod.IsPendingDelete)
            {
                var delete = CreateActionButton(
                    "Delete Mod",
                    16f,
                    buttonTop + buttonHeight + 10,
                    buttonWidth,
                    buttonHeight,
                    () => OpenDeleteConfirm(deleteSettings: false));
                _metaPanel.Append(delete);

                if (_installed.HasConfigFiles)
                {
                    var deleteSettings = CreateActionButton(
                        "Delete + Settings",
                        16f + buttonWidth + buttonGap,
                        buttonTop + buttonHeight + 10,
                        buttonWidth,
                        buttonHeight,
                        () => OpenDeleteConfirm(deleteSettings: true));
                    _metaPanel.Append(deleteSettings);
                }
            }

            if (_mod != null && _mod.IsPendingDelete)
            {
                string pendingNote = _mod.PendingDeleteIncludesSettings
                    ? "This mod is queued for deletion with settings on next launch."
                    : "This mod is queued for deletion on next launch.";
                var pendingText = new UIText(pendingNote, 0.56f, large: false);
                pendingText.Left.Set(16f, 0f);
                pendingText.Top.Set(buttonTop + (buttonHeight * 2f) + 20f, 0f);
                pendingText.Width.Set(-32f, 1f);
                pendingText.IsWrapped = true;
                pendingText.TextColor = new Color(242, 188, 84);
                _metaPanel.Append(pendingText);
            }

        }

        private static UIText CreateMetaText(string text, float top)
        {
            var uiText = new UIText(text, 0.68f, large: false);
            uiText.Left.Set(16f, 0f);
            uiText.Top.Set(top, 0f);
            return uiText;
        }

        private async System.Threading.Tasks.Task FetchImageAsync(NexusMod mod)
        {
            string path = await Service.EnsureNexusImageCachedAsync(mod).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
                _pendingImagePath = path;
        }

        private void ApplyPendingImage()
        {
            if (string.IsNullOrWhiteSpace(_pendingImagePath))
                return;

            object texture = UIRenderer.LoadTexture(_pendingImagePath);
            _pendingImagePath = null;
            if (texture != null)
            {
                _iconTexture = texture;
                RebuildHeaderContents();
            }
        }

        private UITextPanel<string> CreateActionButton(string text, float left, float top, int width, int height, Action onClick)
        {
            var button = new UITextPanel<string>(text ?? string.Empty, 0.62f, large: false);
            button.Width.Set(width, 0f);
            button.Height.Set(height, 0f);
            button.Left.Set(left, 0f);
            button.Top.Set(top, 0f);
            button.BackgroundColor = new Color(54, 72, 122) * 0.95f;
            button.BorderColor = new Color(126, 147, 208);
            button.OnMouseOver += (_, __) =>
            {
                button.BackgroundColor = new Color(73, 94, 171);
                button.BorderColor = Color.White;
            };
            button.OnMouseOut += (_, __) =>
            {
                button.BackgroundColor = new Color(54, 72, 122) * 0.95f;
                button.BorderColor = new Color(126, 147, 208);
            };
            button.OnLeftClick += (_, __) =>
            {
                if (_mod == null || _loading)
                    return;
                onClick();
            };
            return button;
        }

        private void OpenDeleteConfirm(bool deleteSettings)
        {
            string label = deleteSettings ? "Delete Mod + Settings" : "Delete Mod";
            string message = deleteSettings
                ? "Delete the installed mod and remove its saved settings?"
                : "Delete the installed mod but keep its saved settings?";
            OpenState(new ConfirmDialogState(
                Service,
                this,
                InGame,
                label,
                message,
                label,
                () =>
                {
                    Service.UninstallMod(_installed.Id, deleteSettings);
                    OpenState(new NexusModDetailState(Service, PreviousState, InGame, _modId, _mod));
                }));
        }

        private void HandleUndoDeleteClicked()
        {
            if (_installed == null)
                return;

            if (Service.CancelPendingDelete(_installed.Id))
                OpenState(new NexusModDetailState(Service, PreviousState, InGame, _modId, _mod));
        }

        private NexusModFile GetMainFile()
        {
            return _files.FirstOrDefault(f => f.IsPrimary) ?? _files.OrderByDescending(f => f.UploadedTimestamp).FirstOrDefault();
        }

        private static string FormatFile(NexusModFile file)
        {
            string uploaded = file.UploadedTimestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(file.UploadedTimestamp).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "unknown";
            return (file.Name ?? file.FileName) + "  |  v" + (file.Version ?? "?") + "  |  " + uploaded;
        }

        private UIElement CreateSectionRow(string title)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(44f, 0f);
            panel.BackgroundColor = new Color(45, 61, 108) * 0.95f;
            panel.BorderColor = new Color(100, 123, 184);

            var text = new UIText(title, 0.68f, large: true);
            text.HAlign = 0.5f;
            text.VAlign = 0.5f;
            text.Top.Set(-4f, 0f);
            panel.Append(text);
            return panel;
        }

        private UIElement CreateDescriptionPanel(List<DescriptionBlock> blocks)
        {
            var textBlocks = blocks == null
                ? new List<DescriptionBlock>()
                : blocks.Where(b => b.Kind != DescriptionBlockKind.Link).ToList();

            string combinedText = BuildCombinedDescriptionText(textBlocks);
            var wrapped = WrapTextToWidth(combinedText, 760);
            int lineHeight = 18;
            int contentHeight = Math.Max(90, 18 + (wrapped.Count * lineHeight) + 18);

            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(contentHeight, 0f);
            panel.BackgroundColor = new Color(29, 38, 66) * 0.96f;
            panel.BorderColor = new Color(68, 86, 140);

            var label = new UIText(string.Join("\n", wrapped), 0.56f, large: false);
            label.Left.Set(14f, 0f);
            label.Top.Set(12f, 0f);
            label.Width.Set(-28f, 1f);
            label.Height.Set(-16f, 1f);
            label.IsWrapped = true;
            label.TextColor = new Color(206, 214, 236);
            panel.Append(label);

            return panel;
        }

        private static string BuildCombinedDescriptionText(List<DescriptionBlock> blocks)
        {
            if (blocks == null || blocks.Count == 0)
                return "No description provided.";

            var paragraphs = new List<string>();
            foreach (var block in blocks)
            {
                if (block.Kind == DescriptionBlockKind.Separator)
                {
                    paragraphs.Add("----------------------------------------");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(block.Text))
                    paragraphs.Add(block.Text.Trim());
            }

            return paragraphs.Count == 0 ? "No description provided." : string.Join("\n\n", paragraphs);
        }

        private UIElement CreateLinkRow(string text, string url)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(56f, 0f);
            panel.BackgroundColor = new Color(29, 38, 66) * 0.96f;
            panel.BorderColor = new Color(68, 86, 140);

            var label = new UIText((text ?? url ?? "Link"), 0.60f, large: false);
            label.Left.Set(14f, 0f);
            label.Top.Set(10f, 0f);
            label.TextColor = new Color(186, 197, 228);
            panel.Append(label);

            var button = new UITextPanel<string>("Open Link", 0.58f, large: false);
            button.Width.Set(120f, 0f);
            button.Height.Set(34f, 0f);
            button.Left.Set(-134f, 1f);
            button.Top.Set(10f, 0f);
            button.BackgroundColor = new Color(52, 71, 121) * 0.95f;
            button.BorderColor = new Color(126, 147, 208);
            button.OnMouseOver += FadedMouseOver;
            button.OnMouseOut += FadedMouseOut;
            button.OnLeftClick += (_, __) =>
            {
                if (!string.IsNullOrWhiteSpace(url))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            };
            panel.Append(button);

            return panel;
        }

        private static List<string> WrapTextToWidth(string text, int maxWidth)
        {
            var lines = new List<string>();
            foreach (string paragraph in (text ?? string.Empty).Split('\n'))
            {
                string remaining = paragraph.TrimEnd();
                if (string.IsNullOrWhiteSpace(remaining))
                {
                    lines.Add(string.Empty);
                    continue;
                }

                while (Widgets.TextUtil.MeasureWidth(remaining) > maxWidth)
                {
                    int breakAt = -1;
                    for (int i = remaining.Length - 1; i > 0; i--)
                    {
                        if (remaining[i] == ' ' && Widgets.TextUtil.MeasureWidth(remaining.Substring(0, i)) <= maxWidth)
                        {
                            breakAt = i;
                            break;
                        }
                    }

                    if (breakAt <= 0)
                    {
                        breakAt = remaining.Length;
                        for (int i = 1; i < remaining.Length; i++)
                        {
                            if (Widgets.TextUtil.MeasureWidth(remaining.Substring(0, i)) > maxWidth)
                            {
                                breakAt = Math.Max(1, i - 1);
                                break;
                            }
                        }
                    }

                    lines.Add(remaining.Substring(0, breakAt).TrimEnd());
                    remaining = remaining.Substring(breakAt).TrimStart();
                }

                if (remaining.Length > 0)
                    lines.Add(remaining);
            }

            return lines;
        }

        private static List<DescriptionBlock> NormalizeDescriptionBlocks(string rawDescription)
        {
            string text = rawDescription ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return new List<DescriptionBlock>();

            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</p\s*>", "\n\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<p[^>]*>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\[img[^\]]*\].*?\[/img\]", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, @"\[line\]", "\n----------------------------------------\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\[\*\]", "\n- ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\[/?(list|size|b|i|u|s|color|font)[^\]]*\]", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", "", RegexOptions.IgnoreCase);

            var blocks = new List<DescriptionBlock>();
            text = Regex.Replace(text, @"\[url=([^\]]+)\](.*?)\[/url\]", match =>
            {
                string url = match.Groups[1].Value;
                string label = string.IsNullOrWhiteSpace(match.Groups[2].Value) ? url : match.Groups[2].Value;
                blocks.Add(new DescriptionBlock { Kind = DescriptionBlockKind.Link, Text = label.Trim(), Url = url.Trim() });
                return "\n";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            text = Regex.Replace(text, @"\[url\](.*?)\[/url\]", match =>
            {
                string url = match.Groups[1].Value;
                blocks.Add(new DescriptionBlock { Kind = DescriptionBlockKind.Link, Text = url.Trim(), Url = url.Trim() });
                return "\n";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (string paragraph in text.Replace("\r", string.Empty).Split('\n'))
            {
                string cleaned = Regex.Replace(paragraph, @"\s+", " ").Trim();
                if (string.IsNullOrWhiteSpace(cleaned))
                    continue;

                if (cleaned == "----------------------------------------")
                    blocks.Add(new DescriptionBlock { Kind = DescriptionBlockKind.Separator, Text = cleaned });
                else
                    blocks.Add(new DescriptionBlock { Kind = DescriptionBlockKind.Text, Text = cleaned });
            }

            return blocks;
        }

        private sealed class DetailRefreshResult
        {
            public NexusMod Mod { get; set; }
            public List<NexusModFile> Files { get; set; }
            public InstalledModRecord Installed { get; set; }
            public string Status { get; set; }
        }

        private sealed class DescriptionBlock
        {
            public DescriptionBlockKind Kind { get; set; }
            public string Text { get; set; }
            public string Url { get; set; }
        }

        private enum DescriptionBlockKind
        {
            Text,
            Link,
            Separator
        }

        private sealed class NexusIconElement : UIElement
        {
            private readonly object _textureSource;

            public NexusIconElement(object textureSource)
            {
                _textureSource = textureSource;
            }

            protected override void DrawSelf(SpriteBatch spriteBatch)
            {
                base.DrawSelf(spriteBatch);

                Texture2D texture = ResolveTexture(_textureSource);
                if (texture == null)
                    return;

                CalculatedStyle dimensions = GetInnerDimensions();
                float scale = Math.Min(dimensions.Width / texture.Width, dimensions.Height / texture.Height);
                int drawWidth = Math.Max(1, (int)(texture.Width * scale));
                int drawHeight = Math.Max(1, (int)(texture.Height * scale));
                var destination = new Rectangle(
                    (int)(dimensions.X + ((dimensions.Width - drawWidth) * 0.5f)),
                    (int)(dimensions.Y + ((dimensions.Height - drawHeight) * 0.5f)),
                    drawWidth,
                    drawHeight);

                spriteBatch.Draw(texture, destination, Color.White);
            }

            private static Texture2D ResolveTexture(object textureSource)
            {
                if (textureSource is Texture2D texture)
                    return texture;

                var sourceType = textureSource?.GetType();
                var valueProperty = sourceType?.GetProperty("Value");
                return valueProperty?.GetValue(textureSource, null) as Texture2D;
            }
        }
    }

    internal sealed class NexusSettingsState : NativeModsStateBase
    {
        private string _editingApiKey;
        private string _apiKeyBuffer;
        private string _status;
        private string _pendingStatus;

        public NexusSettingsState(NativeModsService service, UIState previousState, bool inGame)
            : base(service, previousState, inGame)
        {
        }

        public override void OnInitialize()
        {
            base.OnInitialize();
            _apiKeyBuffer = Service.NexusAuth.State.ApiKey ?? string.Empty;
            _status = !string.IsNullOrWhiteSpace(Service.NexusAuth.LoginStatus)
                ? Service.NexusAuth.LoginStatus
                : (Service.NexusAuth.HasApiKey ? "Stored API key loaded." : "No Nexus API key configured.");
            _ = ValidateAsync();
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            ApplyPendingStatus();
            if (!string.IsNullOrEmpty(_editingApiKey))
                UpdateApiKeyEditing();
        }

        protected override bool CanGoBack()
        {
            return string.IsNullOrEmpty(_editingApiKey);
        }

        protected override string GetTitle() => "Nexus Settings";

        protected override void RebuildList()
        {
            List.Clear();
            List.Add(CreateInfoRow(_status ?? "No Nexus status available.", 42f));
            List.Add(CreateInfoRow(Service.NexusAuth.HasApiKey
                ? ("User: " + (Service.NexusAuth.State.UserName ?? "unknown") + " | Premium: " + (Service.NexusAuth.State.IsPremium ? "yes" : "no"))
                : "Not authenticated with Nexus.", 40f));
            List.Add(CreateInfoRow("Rate limits: daily " + Service.NexusApi.DailyRemaining + ", hourly " + Service.NexusApi.HourlyRemaining, 36f));

            List.Add(CreateButton("Login With Browser", () => _ = StartBrowserLoginAsync(), 42f));
            List.Add(CreateButton("Validate Stored API Key", () => _ = ValidateAsync(), 42f));

            string apiLabel = string.IsNullOrEmpty(_editingApiKey)
                ? ("Manual API Key: " + MaskApiKey(_apiKeyBuffer))
                : ("Manual API Key: " + _apiKeyBuffer + "_");
            List.Add(CreateButton(apiLabel, BeginApiKeyEditing, 42f));
            List.Add(CreateButton("Save / Validate Manual API Key", () => _ = SaveManualApiKeyAsync(), 42f));
            List.Add(CreateButton("Clear Stored API Key", ClearApiKey, 42f));
        }

        private void BeginApiKeyEditing()
        {
            _editingApiKey = "api";
            Main.clrInput();
            RebuildList();
        }

        private void UpdateApiKeyEditing()
        {
            PlayerInput.WritingText = true;
            Main.instance.HandleIME();
            string newText = Main.GetInputText(_apiKeyBuffer ?? string.Empty);
            if (!string.Equals(newText, _apiKeyBuffer, StringComparison.Ordinal))
            {
                _apiKeyBuffer = newText;
                RebuildList();
            }

            if (InputState.IsKeyJustPressed(KeyCode.Escape))
            {
                _editingApiKey = null;
                Main.clrInput();
                PlayerInput.WritingText = false;
                RebuildList();
                return;
            }

            if (!Main.inputTextEnter)
                return;

            _editingApiKey = null;
            Main.clrInput();
            PlayerInput.WritingText = false;
            _ = SaveManualApiKeyAsync();
        }

        private async System.Threading.Tasks.Task ValidateAsync()
        {
            var user = await Service.ValidateStoredNexusAuthAsync().ConfigureAwait(false);
            _pendingStatus = user != null ? ("Connected as " + user.Name) : (Service.NexusAuth.LoginStatus ?? "No valid Nexus API key.");
        }

        private async System.Threading.Tasks.Task SaveManualApiKeyAsync()
        {
            bool success = await Service.SetManualNexusApiKeyAsync(_apiKeyBuffer).ConfigureAwait(false);
            _pendingStatus = success ? ("Connected as " + Service.NexusAuth.State.UserName) : (Service.NexusAuth.LoginStatus ?? "API key validation failed.");
        }

        private async System.Threading.Tasks.Task StartBrowserLoginAsync()
        {
            string url = await Service.StartNexusBrowserLoginAsync().ConfigureAwait(false);
            _pendingStatus = url != null ? "Browser login started. Finish authorization in your browser." : (Service.NexusAuth.LoginStatus ?? "Failed to start browser login.");
        }

        private void ClearApiKey()
        {
            Service.ClearNexusApiKey();
            _apiKeyBuffer = string.Empty;
            _status = Service.NexusAuth.LoginStatus ?? "API key cleared.";
            RebuildList();
        }

        private void ApplyPendingStatus()
        {
            if (_pendingStatus == null)
                return;

            _status = _pendingStatus;
            _pendingStatus = null;
            RebuildList();
        }

        private static string MaskApiKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "(empty)";
            if (key.Length <= 8)
                return new string('*', key.Length);
            return key.Substring(0, 4) + new string('*', key.Length - 8) + key.Substring(key.Length - 4);
        }
    }

    internal sealed class ConfirmDialogState : NativeModsStateBase
    {
        private readonly string _dialogTitle;
        private readonly string _message;
        private readonly string _primaryLabel;
        private readonly Action _primaryAction;
        private readonly string _secondaryLabel;
        private readonly Action _secondaryAction;

        public ConfirmDialogState(
            NativeModsService service,
            UIState previousState,
            bool inGame,
            string dialogTitle,
            string message,
            string primaryLabel,
            Action primaryAction,
            string secondaryLabel = null,
            Action secondaryAction = null)
            : base(service, previousState, inGame)
        {
            _dialogTitle = dialogTitle;
            _message = message;
            _primaryLabel = primaryLabel;
            _primaryAction = primaryAction;
            _secondaryLabel = secondaryLabel;
            _secondaryAction = secondaryAction;
        }

        protected override string GetTitle() => _dialogTitle;

        protected override void RebuildList()
        {
            List.Clear();
            foreach (string line in (_message ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    List.Add(CreateInfoRow(line, 38f));
            }

            List.Add(CreateButton(_primaryLabel, () => _primaryAction?.Invoke(), 42f));
            if (!string.IsNullOrWhiteSpace(_secondaryLabel))
                List.Add(CreateButton(_secondaryLabel, () => _secondaryAction?.Invoke(), 42f));
            List.Add(CreateButton("Cancel", GoBack, 42f));
        }
    }
}
