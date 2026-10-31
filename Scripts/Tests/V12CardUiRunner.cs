using Godot;
using Magicians.Presentation;
using System.Text.Json;

namespace Magicians.Tests;

public partial class V12CardUiRunner : Node
{
    private async System.Threading.Tasks.Task CheckResolutions(V12MatchController scene)
    {
        foreach (var size in new[] { new Vector2I(960,540), new(1280,720), new(1600,900), new(1920,1080), new(2560,1440), new(3840,2160), new(1024,768), new(2560,1080) })
        {
            GetWindow().Size = size;
            for (var frame = 0; frame < 4; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var viewport = scene.GetViewportRect();
            foreach (var button in Walk(scene).OfType<BaseButton>().Where(b => b.IsVisibleInTree() && b.FindParent("BattleHandStrip") is null))
            {
                var rect = button.GetGlobalRect();
                if (rect.Position.X < -1 || rect.Position.Y < -1 || rect.End.X > viewport.End.X+1 || rect.End.Y > viewport.End.Y+1 || rect.Size.Y < 1)
                    throw new Exception($"Button outside screen at {size}: {button.Name} {rect} viewport={viewport}");
            }
            GD.Print($"V12_RESOLUTION_OK size={size} phase={scene.LastState.GetProperty("decision").GetProperty("kind")}");
        }
        GetWindow().Size = new Vector2I(1920,1080);
        for (var frame = 0; frame < 4; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private static IEnumerable<Node> Walk(Node n) { foreach (var c in n.GetChildren()) { yield return c; foreach (var x in Walk(c)) yield return x; } }
    public override async void _Ready()
    {
        try
        {
            var scene = GD.Load<PackedScene>("res://Scenes/V12/SinglePlayer.tscn").Instantiate<V12MatchController>(); scene.AutoStart = false; AddChild(scene);
            using var cards = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://releases/v1.2/cards.json")));
            var rawCards = cards.RootElement.GetProperty("cards");
            var expected = rawCards.EnumerateObject().ToDictionary(row => row.Name, row => row.Name, StringComparer.Ordinal);
            foreach (var element in new[] { "fire", "water", "poison", "wind", "earth" })
            {
                using var map = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath($"res://docs/cards-v1.2/{element}-name-map.json")));
                foreach (var row in map.RootElement.GetProperty("cards").EnumerateObject())
                    expected[row.Name] = row.Value.GetProperty("display_name").GetString()!;
            }
            foreach (var row in rawCards.EnumerateObject())
            {
                var p = V12CardPresentation.Profile(row.Value);
                if (p.CardId != row.Name || p.ProfileId != "v12:" + row.Name || p.Name != expected[row.Name] || p.CombatCost != row.Value.GetProperty("cost").GetInt32() || p.RulesText != row.Value.GetProperty("text").GetString() || p.RuleNotes.Length != 0)
                    throw new Exception("Stale card presentation: " + row.Name);
                var face = V12CardPresentation.Face(row.Value, 320, 650);
                AddChild(face);
                var view = Walk(face).OfType<CardView>().Single();
                if (view.GetNode<Label>("Margin/Column/Title").Text != expected[row.Name] || view.GetNode<Label>("Margin/Column/Details/Text/Rules").Text != p.DisplayRulesText || face.TooltipText != p.Name + "\n" + p.DisplayRulesText)
                    throw new Exception("Visible card copy differs from approved name: " + row.Name);
                face.QueueFree();
            }
            foreach (var (id, name) in new[] { ("潮涌", "回流"), ("回流", "观潮"), ("泉眼", "润物"), ("泉心", "泉眼"), ("风蚀", "浴风"), ("穿云破甲", "风蚀"), ("万毒爆", "毒爆"), ("毒爆", "以毒淬锋") })
                if (V12CardPresentation.Name(id) != name) throw new Exception("Cascaded display name: " + id);
            string Rules(string id) => V12CardPresentation.Rules(rawCards.GetProperty(id));
            if (!Rules("细流").Contains("浴血狂澜主弹") || !Rules("蚀命").Contains("毒爆与烈性反应") || !Rules("风痕回廊").Contains("已激活积风成灾") || !Rules("风痕回廊").Contains("2层风痕") || !Rules("风痕刻印").Contains("2层风痕") || !Rules("穿云破甲").Contains("风痕引爆") || !Rules("时光倒流").Contains("风痕"))
                throw new Exception("Card references or unchanged mechanic names were mistranslated");
            if (V12CardPresentation.Name("未知扩展牌") != "未知扩展牌") throw new Exception("Unknown card fallback changed");
            GD.Print("V12_DISPLAY_COPY_OK profiles=183 final-names=OK references=OK stable-ids=OK flavor-hidden=OK");
            await scene.StartMatchAsync(334020, new[] { "fire", "water", "earth" });
            if (scene.LastError is not null) throw new Exception("Match startup failed: " + scene.LastError);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var finish = Walk(scene).OfType<Button>().Single(b => b.Text == "结束本面购买");
            var display = new DisplaySettingsDialog(); scene.AddChild(display); display.PopupCentered(new Vector2I(560,300));
            for (var f = 0; f < 4; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (display.Size.Y > 400) throw new Exception($"Settings dialog stretched: {display.Size}");
            display.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await CheckResolutions(scene);
            if (Walk(scene).OfType<Button>().Any(b => b.Text == "查看")) throw new Exception("Redundant per-card inspect buttons");
            var refresh = Walk(scene).OfType<Button>().Single(b => b.Text == "刷新本面 · 1研习");
            if (refresh.GetParent() != finish.GetParent()) throw new Exception("Draft actions are not grouped");
            foreach (var face in Walk(scene).OfType<CardView>())
                if (face.GetNode<Label>("Margin/Column/Title").GetThemeColor("font_color") != Colors.White) throw new Exception("Draft enchantment title highlighted");
            if (finish.GetGlobalRect().End.Y > scene.GetViewportRect().Size.Y + 1) throw new Exception("Draft requires vertical scrolling");
            V12CardPresentation.Open(scene, "完整牌组检查", scene.LastState.GetProperty("own").GetProperty("deck").EnumerateArray());
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var popup = scene.GetChildren().OfType<Window>().Single();
            if (Walk(popup).OfType<CardView>().Count() != 8) throw new Exception("Deck modal omitted instances");
            popup.QueueFree();
            refresh.EmitSignal(Button.SignalName.Pressed);
            for (var tick = 0; tick < 600; tick++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!scene.IsBusy && scene.LastState.GetProperty("decision").GetProperty("kind").GetString() == "draft" && !scene.LastState.GetProperty("decision").GetProperty("can_refresh").GetBoolean()) break;
                if (tick == 599) throw new Exception("Inline refresh did not reach second face");
            }
            if (scene.LastState.GetProperty("own").GetProperty("points").GetInt32() != 1) throw new Exception("Inline refresh charged incorrectly");
            await scene.SubmitAsync("delegate_round");
            if (!scene.LastState.TryGetProperty("combat", out var combat) || !combat.TryGetProperty("draw_pile", out _) || !combat.TryGetProperty("discard_pile", out _)) throw new Exception("Missing own zones");
            if (!Walk(scene).OfType<Button>().Any(b => b.Text.StartsWith("弃牌堆（"))) throw new Exception("Missing discard control");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var lockButton = Walk(scene).OfType<Button>().Single(b => b.Text == "锁定出牌序列");
            var controls = Walk(scene).OfType<HBoxContainer>().Single(c => c.Name == "BattleControls");
            var controlButtons = controls.GetChildren().OfType<Button>().ToArray();
            if (!controlButtons[0].Text.StartsWith("抽牌堆（") || controlButtons[1].Text != "双方状态" || !controlButtons[2].Text.StartsWith("弃牌堆（")) throw new Exception("Wrong battle control order");
            if (Math.Abs(controlButtons[0].GetGlobalRect().Position.X - controls.GetGlobalRect().Position.X) > 1 || Math.Abs(controlButtons[^1].GetGlobalRect().End.X - controls.GetGlobalRect().End.X) > 1) throw new Exception("Battle controls do not fill the row");
            if (Walk(scene).OfType<Button>().Any(b => b.Alignment != HorizontalAlignment.Center)) throw new Exception("Button text must be centered");
            var summary = Walk(scene).OfType<Label>().Single(l => l.Text.Contains("战斗行动值"));
            if (!summary.Text.Contains("攻") || !summary.Text.Contains("防") || !summary.Text.Contains("血") || !summary.Text.Contains("盾")) throw new Exception("Combat overview omitted stats");
            var hand = Walk(scene).OfType<V12HandStrip>().Single();
            if (hand.VerticalScrollMode != ScrollContainer.ScrollMode.Disabled) throw new Exception("Hand must scroll horizontally only");
            var faces = hand.GetChild(0).GetChildren().OfType<Control>().Where(c => c is not BaseButton).ToArray();
            if (faces.Any(c => c.Position.Y != 0)) throw new Exception("Battle cards wrapped into another row");
            await CheckResolutions(scene);
            if (lockButton.GetGlobalRect().End.Y > scene.GetViewportRect().Size.Y) throw new Exception("Battle controls overflow");
            Walk(scene).OfType<Button>().Single(b => b.Text == "双方状态").EmitSignal(Button.SignalName.Pressed);
            var statusWindow = scene.GetChildren().OfType<Window>().Single(w => w.Title == "双方当前状态");
            if (!Walk(statusWindow).OfType<Label>().Any(l => l.Text == "对手状态")) throw new Exception("Missing opponent status");
            statusWindow.QueueFree();
            await scene.SubmitAsync("play"); await scene.SubmitAsync("play");
            var logs = Walk(scene).OfType<RichTextLabel>().ToArray();
            if (logs.Any(l => l.Text.Contains("左方") || l.Text.Contains("右方"))) throw new Exception("Positional names leaked into logs");
            if (!logs.Any(l => l.Text.Contains("AI "))) throw new Exception("Logs lack concrete player names");
            if (logs.Any(l => l.CustomMinimumSize.Y > 64)) throw new Exception("Logs should have two-line height");
            scene.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GD.Print("V12_CARD_UI_OK profiles=183 draft-fit=OK deck-modal=8 own-piles=OK"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
