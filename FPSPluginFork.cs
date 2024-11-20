using Dalamud.Plugin;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.Gui.Dtr;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using Dalamud.Interface.ManagedFontAtlas;

namespace FPSPluginFork {
    public class FPSPluginFork : IDalamudPlugin {
        public string Name => "FPS Plugin";
        public FPSPluginConfig PluginConfig { get; }

        private bool drawConfigWindow;
        
        private List<float> fpsHistory;
        private Stopwatch fpsHistoryInterval;
        private string fpsText;
        private Vector2 windowSize = Vector2.One;
        private IDtrBarEntry dtrEntry;
        private float maxSeenFps;

        private IFontHandle font;

        [PluginService] public static  ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] public static  IFramework Framework { get; private set; } = null!;
        [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
        [PluginService] public static IPluginLog PluginLog { get; private set; } = null!;

        public void Dispose() {
            PluginInterface.UiBuilder.Draw -= this.BuildUI;
            PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
            Framework.Update -= this.OnFrameworkUpdate;
            fpsHistoryInterval?.Stop();
            dtrEntry?.Remove();
            RemoveCommands();
        }

        public FPSPluginFork() {
            this.PluginConfig = (FPSPluginConfig) PluginInterface.GetPluginConfig() ?? new FPSPluginConfig();
            this.PluginConfig.Init(this);
            fpsText = string.Empty;
            fpsHistory = new List<float>();

            fpsHistoryInterval = new Stopwatch();
            fpsHistoryInterval.Start();
            SetupCommands();
            PluginInterface.UiBuilder.Draw += this.BuildUI;
            PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
            Framework.Update += OnFrameworkUpdate;

            BuildFont();
        }

        private string FormatFpsValue(float value) {
            if (maxSeenFps > 1000) return PluginConfig.ShowDecimals ? $"{value,8:####0.00}" : $"{value,5:####0}";
            if (maxSeenFps > 100) return PluginConfig.ShowDecimals ? $"{value,7:###0.00}" : $"{value,4:###0}";
            return PluginConfig.ShowDecimals ? $"{value,6:##0.00}" : $"{value,3:##0}";
        }

        private unsafe void OnFrameworkUpdate(IFramework dFramework) {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            try {
                if (!font.Available) return;
                if (PluginConfig.UseDtr && fpsText != null) {
                    dtrEntry ??= DtrBar.Get("FPS Display");
                    dtrEntry.Shown = PluginConfig.Enable;
                    dtrEntry.Text = fpsText;
                    dtrEntry.OnClick = PluginConfig.DtrOpenSettings ? OpenConfigUi : null;
                    dtrEntry.Tooltip = PluginConfig.DtrTooltip && fpsHistory.Count > 0 ? $"Average:{FormatFpsValue(fpsHistory.Average())}\nMinimum:{FormatFpsValue(fpsHistory.Min())}" : null;
                } else {
                    if (dtrEntry != null) dtrEntry.Shown = false;
                }
                
                if (fpsHistoryInterval.ElapsedMilliseconds > 1000) {
                    fpsHistoryInterval.Restart();
                    // FPS values are only updated in memory once per second.
                    var fps = framework->FrameRate;
                    var windowInactive = framework->WindowInactive;
                    if (fps > maxSeenFps) maxSeenFps = fps;

                    fpsText = string.Empty;
                    if (!PluginConfig.NoLabels && !PluginConfig.AlternativeFPSLabel) fpsText += "FPS:";
                    fpsText += $"{FormatFpsValue(fps)}";
                    if (!PluginConfig.NoLabels && PluginConfig.AlternativeFPSLabel) fpsText += "fps";
                    if (PluginConfig.ShowAverage || PluginConfig.ShowMinimum || PluginConfig.DtrTooltip) {
                        if (!windowInactive) fpsHistory.Add(fps);

                        if (fpsHistory.Count > PluginConfig.HistorySnapshotCount) {
                            fpsHistory.RemoveRange(0, fpsHistory.Count - PluginConfig.HistorySnapshotCount);
                        }

                        if (PluginConfig.ShowAverage && fpsHistory.Count > 0) {
                            fpsText +=  " / ";
                            if (!PluginConfig.NoLabels) fpsText += "Avg:";
                            fpsText += $"{FormatFpsValue(fpsHistory.Average())}";
                        }

                        if (PluginConfig.ShowMinimum && fpsHistory.Count > 0) {
                            fpsText += " / ";
                            if (!PluginConfig.NoLabels) fpsText += "Min:";
                            fpsText += $"{FormatFpsValue(fpsHistory.Min())}";
                        }
                    }
#if DEBUG
                    if (!string.IsNullOrEmpty(PluginConfig.TestText)) {
                        fpsText = PluginConfig.TestText;
                    }
#endif
                }

            } catch (Exception ex) {
                PluginLog.Error(ex.Message);
            }
        }

        public void SetupCommands() {
            CommandManager.AddHandler("/pfps", new Dalamud.Game.Command.CommandInfo(OnConfigCommandHandler) {
                HelpMessage = $"Open config window for {this.Name}. /pfps [show|hide|toggle|reset]",
                ShowInHelp = true
            });
        }

        private void OpenConfigUi() {
            OnConfigCommandHandler(null, null);
        }

        public void OnConfigCommandHandler(string command, string args) {
            if (args != null) {
                switch (args.ToLower()) {
                    case "t":
                    case "toggle": {
                        PluginConfig.Enable = !PluginConfig.Enable;
                        break;
                    }
                    case "s":
                    case "show": {
                        PluginConfig.Enable = true;
                        break;
                    }
                    case "h":
                    case "hide": {
                        PluginConfig.Enable = false;
                        break;
                    }
                    case "r":
                    case "reset": {
                        fpsHistory.Clear();
                        break;
                    }
                    default: {
                        drawConfigWindow = true;
                        break;
                    }
                }

                PluginConfig.Save();
            } else {
                drawConfigWindow = true;
            }
        }

        public void RemoveCommands() {
            CommandManager.RemoveHandler("/pfps");
        }

        private string GetFontPath(FPSPluginFont font) {
            return font switch {
                FPSPluginFont.DalamudDefault => Path.Combine(PluginInterface.DalamudAssetDirectory.FullName, "UIRes", "NotoSansCJKjp-Medium.otf"),
                _ => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "font.ttf"),
            };
        }
        
        private void BuildFont() {
            this.font = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
                e.OnPreBuild(tk => {
                    var config = new SafeFontConfig { SizePx = PluginConfig.FontSize };
                    var fontFile = GetFontPath(PluginConfig.Font);
                    if (File.Exists(fontFile)) {
                        config.MergeFont = tk.AddFontFromFile(fontFile, config);
                        tk.AddGameSymbol(config);
                        tk.AttachExtraGlyphsForDalamudLanguage(config);
                        }
                    else {
                        PluginLog.Error($"Font failed to load. {fontFile} does not exist.");
                    }
                }
            ));

            
        }
        
        private void BuildUI() {
            drawConfigWindow = drawConfigWindow && PluginConfig.DrawConfigUI();

            if (!PluginConfig.Enable || PluginConfig.UseDtr || string.IsNullOrEmpty(fpsText)) return;

            ImGui.SetNextWindowBgAlpha(PluginConfig.Alpha);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PluginConfig.WindowPadding);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, PluginConfig.WindowCornerRounding);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
            var stylePopCount = 6;
            if (PluginConfig.BorderSize >= 0) {
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, PluginConfig.BorderSize);
                stylePopCount++;
            }
            var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoFocusOnAppearing;

            if (PluginConfig.Locked) {
                flags |= ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus;
            }

            if (font.Available) font.Push();
            if (windowSize == Vector2.Zero) {
                windowSize = ImGui.CalcTextSize(fpsText) + (ImGui.GetStyle().WindowPadding * 2);
            }
            
            ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
            ImGui.Begin("FPS##fpsPluginMonitorWindow", flags);
            ImGui.TextColored(PluginConfig.Colour, fpsText);
            ImGui.End();
            ImGui.PopStyleVar(stylePopCount);
            if (font.Available) font.Pop();
        }
    }
}
