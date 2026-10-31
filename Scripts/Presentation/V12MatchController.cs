using Godot;
using Magicians.Application;
using System.Text.Json;

namespace Magicians.Presentation;

public partial class V12MatchController : Control
{
    private V12MatchRuntime? _runtime;
    private readonly Label _title = new() { Text = "Magicians! · v1.2 单人赛事" };
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 3, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly Label _standings = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 3, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly VBoxContainer _actions = new();
    private readonly VBoxContainer _deck = new();
    private readonly HBoxContainer _toolbar = new();
    private readonly RichTextLabel _log = new() { CustomMinimumSize = new Vector2(0, 64), FitContent = false, ScrollFollowing = true };
    private readonly HBoxContainer _logs = new();
    private readonly RichTextLabel _missileLog = new() { CustomMinimumSize = new Vector2(0, 64), FitContent = false, ScrollFollowing = true };
    private readonly List<BaseButton> _buttons = new();
    private readonly List<string> _selected = new();
    private Window? _deleteWindow;
    private VBoxContainer? _deleteBody;
    private readonly Dictionary<string, Control> _selectionFaces = new();
    private bool _exiting;
    private string _root = "";
    public bool IsBusy { get; private set; }
    public JsonElement LastState { get; private set; }
    public string? LastError { get; private set; }
    private static readonly string[] StyleIds = { "flexible", "fire", "water", "earth", "wind", "poison" };
    public bool AutoStart { get; set; } = true;

    public override void _Ready()
    {
        Theme = new Theme { DefaultFontSize = 26 };
        _log.AddThemeFontSizeOverride("normal_font_size", 20);
        _missileLog.AddThemeFontSizeOverride("normal_font_size", 20);
        _root = ClientPaths.DataRoot;
        var background = new ColorRect { Color = new Color("101824"), MouseFilter = MouseFilterEnum.Ignore };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(background);
        var margin = new MarginContainer(); margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 24);
        AddChild(margin);
        var scroll = new VBoxContainer(); margin.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(body); body.SizeFlagsVertical = SizeFlags.ExpandFill;
        var header = new HBoxContainer(); body.AddChild(header);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill; _title.AddThemeFontSizeOverride("font_size", 30); header.AddChild(_title);
        var settings = new Button { Text = "显示设置" }; header.AddChild(settings);
        settings.Pressed += OpenDisplaySettings;
        var deckButton = new Button { Name = "CurrentDeck", Text = "当前牌组" }; header.AddChild(deckButton);
        deckButton.Pressed += () => { if (LastState.ValueKind != JsonValueKind.Undefined && LastState.TryGetProperty("own", out var own) && own.ValueKind != JsonValueKind.Null) V12CardPresentation.Open(this, "当前完整牌组（含已打出的专精与传奇）", own.GetProperty("deck").EnumerateArray()); };
        body.AddChild(_standings); body.AddChild(_status); body.AddChild(_toolbar);
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(columns);
        _actions.SizeFlagsHorizontal = SizeFlags.ExpandFill; columns.AddChild(_actions);
        _actions.SizeFlagsVertical = SizeFlags.ExpandFill;
        AddChild(_deck); _deck.Visible = false;
        foreach (var pair in new[] { ("战斗日志", _log), ("飞弹日志", _missileLog) })
        {
            var panel = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddChild(new Label { Text = pair.Item1 }); panel.AddChild(pair.Item2); _logs.AddChild(panel);
        }
        body.AddChild(_logs); _logs.Visible = false;
        if (AutoStart) _ = StartRandomMatchAsync();
    }

    public override void _ExitTree() { _exiting = true; _runtime?.Dispose(); }

    public Task StartRandomMatchAsync()
    {
        var seed = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var random = new Random(seed);
        var styles = Enumerable.Range(0, 3).Select(_ => StyleIds[random.Next(StyleIds.Length)]).ToArray();
        return StartMatchAsync(seed, styles);
    }

    public async Task StartMatchAsync(int seed, string[] styles)
    {
        if (IsBusy) return;
        await Run(async () =>
        {
            _runtime?.Dispose(); _runtime = new V12MatchRuntime(_root);
            return await _runtime.Start(seed, styles);
        });
    }

    public async Task SubmitAsync(string kind, int? slot = null, int? index = null, string? uid = null, string[]? uids = null)
    {
        if (IsBusy || _runtime is null) return;
        await Run(() => _runtime.Submit(kind, slot, index, uid, uids));
    }

    private async Task Run(Func<Task<JsonElement>> operation)
    {
        IsBusy = true; LastError = null;
        foreach (var button in _buttons) if (IsInstanceValid(button)) button.Disabled = true;
        TextureRect? cover = null;
        CanvasLayer? coverLayer = null;
        try
        {
        if (DisplayServer.GetName() != "headless" && Engine.GetProcessFrames() > 2)
        {
            using var shot = GetViewport().GetTexture().GetImage();
            if (!shot.IsEmpty())
            {
                cover = new TextureRect { Texture = ImageTexture.CreateFromImage(shot), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Stop, StretchMode = TextureRect.StretchModeEnum.Scale };
                coverLayer = new CanvasLayer { Layer = 1000 };
                AddChild(coverLayer); coverLayer.AddChild(cover);
                cover.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            }
        }
            var data = await operation();
            if (_exiting) return;
            LastState = data; Render(data);
            // Containers and deferred card drawing can trigger several further sorts.
            // Keep the independent overlay until the geometry has converged.
            string previous = "";
            var stable = 0;
            for (var frame = 0; frame < 30 && stable < 4; frame++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var geometry = LayoutGeometry(this);
                stable = geometry == previous ? stable + 1 : 0;
                previous = geometry;
            }
        }
        catch (Exception error)
        {
            if (_exiting) return;
            LastError = error.Message; _status.Text = "赛事处理失败：" + error.Message;
            Clear(_toolbar); _buttons.Clear();
            Button(_toolbar, "重新开始", Restart); Button(_toolbar, "返回主菜单", Back);
        }
        finally
        {
            if (cover is not null && IsInstanceValid(cover))
            {
                var texture = cover.Texture;
                cover.Hide(); cover.Texture = null; texture?.Dispose(); coverLayer?.QueueFree();
                if (!_exiting) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            IsBusy = false;
        }
    }

    private static string LayoutGeometry(Node node)
    {
        var geometry = new System.Text.StringBuilder();
        void Visit(Node parent)
        {
            foreach (var child in parent.GetChildren())
            {
                if (child is CanvasLayer) continue;
                if (child is Control control && control.IsVisibleInTree())
                    geometry.Append(control.GetInstanceId()).Append(control.Position).Append(control.Size);
                Visit(child);
            }
        }
        Visit(node);
        return geometry.ToString();
    }

    private void Render(JsonElement data)
    {
        Clear(_actions); Clear(_deck); Clear(_toolbar); _buttons.Clear(); _selected.Clear(); _selectionFaces.Clear();
        _logs.Visible = data.TryGetProperty("combat", out _) || data.GetProperty("status").GetString() == "finished" || data.GetProperty("decision").GetProperty("kind").GetString() == "round";
        var round = data.GetProperty("round").GetInt32();
        _title.Text = $"Magicians! · v1.2 · 第 {round} 轮";
        _standings.Text = string.Join("    |    ", data.GetProperty("standings").EnumerateArray().OrderBy(x => x.GetProperty("pid").GetInt32()).Select(p =>
            $"{PlayerName(p.GetProperty("pid"))} 赛事生命 {p.GetProperty("hp")} · {Element(p.GetProperty("spec").GetString())} · {V12CardPresentation.Name(p.GetProperty("route").GetString())}"));
        _standings.Text += "\n本轮配对：" + string.Join("；", data.GetProperty("pairs").EnumerateArray().Select(pair => PlayerName(pair[0]) + " 对 " + PlayerName(pair[1])));
        _standings.Text += "\n" + string.Join("；", data.GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() is "specialization" or "route").Select(e =>
                PlayerName(e.GetProperty("pid")) + (e.GetProperty("kind").GetString() == "route" ? " 选择路线 " + V12CardPresentation.Name(e.GetProperty("name").GetString()) : " 获得" + Element(e.GetProperty("element").GetString()) + "系专精")));
        if (data.TryGetProperty("previous_battles", out var previous) && previous.GetArrayLength() > 0)
            _standings.Text += "\n上轮赛果：" + string.Join("；", previous.EnumerateArray().Select(b =>
                $"{PlayerName(b.GetProperty("a"))} 对 {PlayerName(b.GetProperty("b"))}：" +
                (b.GetProperty("winner").ValueKind == JsonValueKind.Null ? "平局" : PlayerName(b.GetProperty("winner").GetInt32() == 0 ? b.GetProperty("a") : b.GetProperty("b")) + " 获胜")));
        Button(_toolbar, "返回主菜单", Back); Button(_toolbar, "重新开始", Restart);
        var completed = data.GetProperty("completed_rounds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var canReview = data.GetProperty("status").GetString() == "finished" || data.GetProperty("decision").GetProperty("kind").GetString() == "round";
        if (canReview && completed.Contains(round))
            Button(_toolbar, "查看本轮复盘", () => _ = ShowReviewAsync(round));
        var own = data.GetProperty("own");
        if (own.ValueKind != JsonValueKind.Null)
        {
            _status.Text = $"研习 {own.GetProperty("points")} · 本轮收入 {own.GetProperty("income")}（继承 {own.GetProperty("carry")}，连胜败奖励 {own.GetProperty("bonus")}） · 牌组 {own.GetProperty("deck").GetArrayLength()} 张";
            _deck.AddChild(new Label { Text = "你的咒语书" });
            foreach (var card in own.GetProperty("deck").EnumerateArray())
            {
                var label = new Label { Text = $"{V12CardPresentation.Name(card.GetProperty("name").GetString())}  {card.GetProperty("cost")}费", TooltipText = CardText(card) };
                _deck.AddChild(label);
            }
        }
        if (data.GetProperty("status").GetString() == "finished")
        {
            _status.Text = "赛事结束";
            foreach (var p in data.GetProperty("placements").EnumerateArray())
                _actions.AddChild(new Label { Text = $"第 {p.GetProperty("rank")} 名 · {PlayerName(p.GetProperty("pid"))} · 赛事生命 {p.GetProperty("hp")} · {Element(p.GetProperty("spec").GetString())} {V12CardPresentation.Name(p.GetProperty("route").GetString())}" });
            ShowRoundLog(data); return;
        }
        Button(_toolbar, "交给 AI 直到赛事结束", () => _ = SubmitAsync("delegate_match"));
        var d = data.GetProperty("decision"); var kind = d.GetProperty("kind").GetString();
        if (kind is "draft" or "refresh" or "delete")
            Button(_toolbar, "本轮构筑交给 AI", () => _ = SubmitAsync("delegate_round"));
        if (kind is "play" or "select")
            Button(_toolbar, "本场战斗交给 AI", () => _ = SubmitAsync("delegate_battle"));
        switch (kind)
        {
            case "draft":

                CardOptions(d.GetProperty("offers").EnumerateArray().ToArray(), card => {
                    var slot = card.GetProperty("slot").GetInt32();
                    var price = card.GetProperty("price"); var replaces = card.GetProperty("replaces");
                    return ($"购买 · {price}研习" + (replaces.ValueKind == JsonValueKind.Null ? "" : " · 替换" + V12CardPresentation.Name(replaces.GetString())),
                        (Action)(() => _ = SubmitAsync("buy", slot: slot)), !card.GetProperty("affordable").GetBoolean());
                });
                var draftControls = new HBoxContainer(); _actions.AddChild(draftControls);
                Button(draftControls, "结束本面购买", () => _ = DraftTransition("finish"));
                Button(draftControls, "刷新本面 · 1研习", () => _ = DraftTransition("refresh"), !d.GetProperty("can_refresh").GetBoolean());
                if (d.GetProperty("can_delete").GetBoolean())
                {
                    Button(draftControls, "删牌", OpenDeleteWindow);
                    if (round >= 7 && d.GetProperty("deletion").GetProperty("fee").GetInt32() == 0)
                    {
                        var deletionButton = (Button)draftControls.GetChild(draftControls.GetChildCount()-1);
                        var highlight = new StyleBoxFlat { BgColor = new Color("66501e"), BorderColor = new Color("ffd166") };
                        highlight.SetBorderWidthAll(3);
                        foreach (var state in new[] { "normal", "hover", "pressed", "focus" }) deletionButton.AddThemeStyleboxOverride(state, highlight);
                        deletionButton.TooltipText = "本轮免费删牌尚未使用";
                    }
                }
                if (_deleteWindow is not null && IsInstanceValid(_deleteWindow)) PopulateDeleteWindow();
                break;
            case "refresh":
                _status.Text += "\n本轮可以刷新一次，已购买的位置保持空缺，两面共用免费购买额度。";
                Button(_actions, $"支付 {d.GetProperty("cost")} 研习并刷新", () => _ = SubmitAsync("refresh"));
                Button(_actions, "不刷新，进入战斗", () => _ = SubmitAsync("keep_face")); break;
            case "play":
                ShowCombat(data);

                CardOptions(d.GetProperty("hand").EnumerateArray().ToArray(), card => {
                    var uid = card.GetProperty("uid").GetString();
                    return ($"打出 · {card.GetProperty("payable_cost")}费", (Action)(() => _ = SubmitAsync("play", uid: uid)),
                        card.GetProperty("payable_cost").GetInt32() > d.GetProperty("energy").GetInt32());
                });
                Button(_actions, "锁定出牌序列", () => _ = SubmitAsync("play")); break;
            case "select":
                ShowCombat(data);
                var maximum = d.GetProperty("count").GetInt32(); var optional = d.GetProperty("optional").GetBoolean();
                _status.Text += $"\n{Purpose(d.GetProperty("purpose").GetString())}：选择{(optional ? "至多" : "恰好")} {maximum} 张；点击顺序决定置顶顺序。";
                var order = new Label { Text = "选择顺序：" }; _actions.AddChild(order);
                CardOptions(d.GetProperty("candidates").EnumerateArray().ToArray(), card => {
                    var uid = card.GetProperty("uid").GetString()!;
                    return ("选择 / 取消", (Action)(() => {
                        if (_selected.Contains(uid)) _selected.Remove(uid); else if (_selected.Count < maximum) _selected.Add(uid);
                        UpdateSelectionBorders();
                        order.Text = "选择顺序：" + string.Join(" → ", _selected.Select(id => V12CardPresentation.Name(d.GetProperty("candidates").EnumerateArray().First(c => c.GetProperty("uid").GetString() == id).GetProperty("name").GetString())));
                    }), false);
                });
                Button(_actions, "确认选择", () => {
                    if (_selected.Count > maximum || (!optional && _selected.Count != maximum))
                    { order.Text = $"请选择{(optional ? "至多" : "恰好")} {maximum} 张。"; return; }
                    _ = SubmitAsync("select", uids: _selected.ToArray());
                }); break;
            case "round":
                ShowRoundLog(data);
                var hp = own.GetProperty("hp").GetInt32();
                _status.Text += hp <= 0 ? "\n你已淘汰，可以继续观战至冠军产生。" : "\n本轮战斗已结算，下一轮将重新公布对手。";
                Button(_actions, hp <= 0 ? "继续观战下一轮" : "进入下一轮构筑", () => _ = SubmitAsync("next_round")); break;
        }
        if (data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) _status.Text += "\n操作未执行：" + error.GetString();
    }

    private void ShowCombat(JsonElement data)
    {
        var combat = data.GetProperty("combat");
        var piles = new HBoxContainer { Name = "BattleControls", SizeFlagsHorizontal = SizeFlags.ExpandFill }; _actions.AddChild(piles);
        Button(piles, $"抽牌堆（{combat.GetProperty("draw_pile").GetArrayLength()}）", () => V12CardPresentation.Open(this, "抽牌堆 · 无序查看", combat.GetProperty("draw_pile").EnumerateArray()));
        Button(piles, "双方状态", () => OpenStatus(combat));
        Button(piles, $"弃牌堆（{combat.GetProperty("discard_pile").GetArrayLength()}）", () => V12CardPresentation.Open(this, "弃牌堆", combat.GetProperty("discard_pile").EnumerateArray()));
        var energy = data.GetProperty("decision").TryGetProperty("energy", out var available) ? available.ToString() : combat.GetProperty("public")[combat.GetProperty("human_seat").GetInt32()].GetProperty("energy").ToString();
        _status.Text = $"战斗行动值 {combat.GetProperty("time")} · 当前可用能量 {energy}";
        foreach (var state in combat.GetProperty("public").EnumerateArray())
        {
            var participant = combat.GetProperty("participants")[state.GetProperty("seat").GetInt32()];
            _status.Text += $"   |   {LogPlayerName(participant)} 攻{state.GetProperty("attack")} 防{state.GetProperty("defense")} 血{state.GetProperty("hp")} 盾{state.GetProperty("shield")}";
        }
        _status.Text += "\n已规划：" + string.Join(" → ", combat.GetProperty("planned").EnumerateArray().Select(x => V12CardPresentation.Name(x.GetString())));
        var events = combat.GetProperty("log").EnumerateArray().ToArray();
        _log.Text = string.Join("\n", events.Where(e => !IsMissile(e)).Select(e => EventText(e, combat.GetProperty("participants")[0], combat.GetProperty("participants")[1])));
        _missileLog.Text = string.Join("\n", events.Where(IsMissile).Select(e => EventText(e, combat.GetProperty("participants")[0], combat.GetProperty("participants")[1])));
    }

    private void ShowRoundLog(JsonElement data)
    {
        _log.Text = string.Join("\n", data.GetProperty("last_battles").EnumerateArray().Select(b =>
            $"{PlayerName(b.GetProperty("a"))} 对 {PlayerName(b.GetProperty("b"))}：{(b.GetProperty("winner").ValueKind == JsonValueKind.Null ? "双方倒下" : PlayerName(b.GetProperty(b.GetProperty("winner").GetInt32() == 0 ? "a" : "b")) + "获胜")}，剩余战斗生命 {b.GetProperty("hp")}"));
    }

    private async Task ShowReviewAsync(int round)
    {
        if (IsBusy || _runtime is null || LastState.GetProperty("round").GetInt32() != round) return;
        IsBusy = true;
        try
        {
            var result = await _runtime.Review(round);
            if (_exiting) return;
            var review = result.GetProperty("review");
            var window = new Window { Name = "RoundReview", Title = $"第 {round} 轮复盘", Transient = true, Exclusive = true, Unresizable = true };
            AddChild(window);
            var body = new VBoxContainer(); window.AddChild(body); body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            body.OffsetLeft = 20; body.OffsetRight = -20; body.OffsetTop = 16; body.OffsetBottom = -16;
            body.AddChild(new Label { Text = $"第 {round} 轮复盘（仅公开已结算事件）" });
            var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(columns);
            foreach (var missile in new[] { false, true })
            {
                var panel = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; columns.AddChild(panel);
                panel.AddChild(new Label { Text = missile ? "飞弹日志" : "战斗日志" });
                var log = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                panel.AddChild(log);
                log.Text = string.Join("\n", review.GetProperty("battles").EnumerateArray().Select(b =>
                    $"{LogPlayerName(b.GetProperty("a"))} 对 {LogPlayerName(b.GetProperty("b"))}\n" + string.Join("\n", b.GetProperty("log").EnumerateArray()
                        .Where(e => IsMissile(e) == missile).Select(e => EventText(e, b.GetProperty("a"), b.GetProperty("b"))))));
            }
            var close = new Button { Text = "关闭复盘", Alignment = HorizontalAlignment.Center }; close.Pressed += window.QueueFree; body.AddChild(close);
            window.CloseRequested += window.QueueFree;
            var size = GetWindow().Size; window.PopupCentered(new Vector2I(Math.Min(1500, size.X - 48), Math.Min(900, size.Y - 48)));
        }
        catch (Exception error) { if (!_exiting) _status.Text += "\n复盘读取失败：" + error.Message; }
        finally { IsBusy = false; }
    }

    private void CardOptions(JsonElement[] cards, Func<JsonElement, (string Text, Action Action, bool Disabled)> option)
    {
        if (LastState.TryGetProperty("combat", out _)) { BattleCards(cards, option); return; }
        // A rack consumes only the remaining container area. Child card minimum
        // sizes must never push the footer, logs or action buttons offscreen.
        var rack = new Control { Name = "CardRack", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _actions.AddChild(rack);
        var navigation = new HBoxContainer(); _actions.AddChild(navigation);
        var page = 0;
        void Draw()
        {
            if (_exiting || !rack.IsInsideTree() || rack.IsQueuedForDeletion()) return;
            Clear(rack); Clear(navigation);
            if (rack.Size.X < 1 || rack.Size.Y < 1) return;
            const int columns = 4;
            var count = Math.Min(8, cards.Length - page * 8);
            var rows = count > 4 ? 2 : 1;
            var cellWidth = rack.Size.X / columns;
            var cellHeight = rack.Size.Y / rows;
            var height = Math.Max(1, Math.Min(cellHeight - 46, (cellWidth - 20) * 1.618f));
            var width = height / 1.618f;
            var index = 0;
            foreach (var card in cards.Skip(page * 8).Take(8))
            {
                var x = (index % columns) * cellWidth + (cellWidth - width) / 2;
                var y = (index / columns) * cellHeight;
                var face = V12CardPresentation.Face(card, width, height, compact: true, combat: false);
                rack.AddChild(face); face.Position = new Vector2(x, y);
                var selected = option(card);
                var button = new Button { Text = selected.Text, Disabled = selected.Disabled,
                    Position = new Vector2(x, y + height + 3), Size = new Vector2(width, 36),
                    Theme = new Theme { DefaultFontSize = 20 }, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    TooltipText = selected.Text };
                button.Pressed += () => { if (!IsBusy) selected.Action(); };
                rack.AddChild(button); _buttons.Add(button); index++;
            }
            if (cards.Length > 8)
            {
                Button(navigation, "上一页", () => { page--; Draw(); }, page == 0);
                navigation.AddChild(new Label { Text = $"{page+1} / {(cards.Length+7)/8}" });
                Button(navigation, "下一页", () => { page++; Draw(); }, (page+1)*8 >= cards.Length);
            }
        }
        rack.Resized += Draw;
        Callable.From(Draw).CallDeferred();
    }

    private void BattleCards(JsonElement[] cards, Func<JsonElement, (string Text, Action Action, bool Disabled)> option)
    {
        var strip = new V12HandStrip { Name = "BattleHandStrip", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, HorizontalScrollMode = ScrollContainer.ScrollMode.Auto };
        _actions.AddChild(strip);
        var content = new Control(); strip.AddChild(content);
        void Draw()
        {
            if (_exiting || !strip.IsInsideTree() || strip.IsQueuedForDeletion()) return;
            Clear(content); _selectionFaces.Clear();
            var height = Math.Max(1, Math.Min(600, strip.Size.Y - 58)); var width = height / 1.618f;
            content.CustomMinimumSize = new Vector2(Math.Max(1, cards.Length * (width + 18)), height + 44);
            var x = 0f;
            foreach (var card in cards)
            {
                var face = V12CardPresentation.Face(card, width, height, compact: true, combat: true);
                content.AddChild(face); face.Position = new Vector2(x, 0);
                if (card.TryGetProperty("uid", out var id)) _selectionFaces[id.GetString()!] = face;
                var selected = option(card);
                var button = new Button { Text = selected.Text, Disabled = selected.Disabled, Position = new Vector2(x, height + 4), Size = new Vector2(width, 36) };
                button.Pressed += () => { if (!IsBusy) selected.Action(); }; content.AddChild(button); _buttons.Add(button);
                x += width + 18;
            }
            UpdateSelectionBorders();
        }
        strip.Resized += Draw; Callable.From(Draw).CallDeferred();
    }

    private void UpdateSelectionBorders()
    {
        foreach (var pair in _selectionFaces)
        {
            if (!IsInstanceValid(pair.Value)) continue;
            var face = pair.Value.GetChild<CardView>(0);
            var style = (StyleBoxFlat)face.GetThemeStylebox("panel").Duplicate();
            var selected = _selected.Contains(pair.Key);
            style.BorderColor = selected ? new Color("ffd166") : new Color("526176");
            style.SetBorderWidthAll(selected ? 5 : 2);
            face.AddThemeStyleboxOverride("panel", style);
        }
    }

    private void OpenDeleteWindow()
    {
        if (_deleteWindow is not null && IsInstanceValid(_deleteWindow)) return;
        _deleteWindow = new Window { Title = "删牌", Name = "DeleteCards", Transient = true, Exclusive = true, Unresizable = true };
        AddChild(_deleteWindow);
        _deleteBody = new VBoxContainer(); _deleteWindow.AddChild(_deleteBody); _deleteBody.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _deleteBody.OffsetLeft = 16; _deleteBody.OffsetRight = -16; _deleteBody.OffsetTop = 16; _deleteBody.OffsetBottom = -16;
        _deleteWindow.CloseRequested += CloseDeleteWindow;
        PopulateDeleteWindow();
        var size = GetWindow().Size; _deleteWindow.PopupCentered(new Vector2I(Math.Min(1600, size.X-48), Math.Min(960, size.Y-48)));
    }

    private void CloseDeleteWindow()
    {
        var window = _deleteWindow; _deleteWindow = null; _deleteBody = null;
        if (window is not null && IsInstanceValid(window))
        {
            window.Hide(); RemoveChild(window); window.QueueFree();
        }
    }

    private void PopulateDeleteWindow()
    {
        if (_deleteBody is null) return;
        Clear(_deleteBody);
        var decision = LastState.GetProperty("decision");
        var available = decision.TryGetProperty("deletion", out var rule) && rule.ValueKind != JsonValueKind.Null;
        var legal = available ? rule.GetProperty("legal").EnumerateArray().Select(x => x.GetInt32()).ToHashSet() : new HashSet<int>();
        _deleteBody.AddChild(new Label { Text = available ? $"删除费用 {rule.GetProperty("fee")} · 剩余次数 {rule.GetProperty("remaining")}" : "本轮暂无可删除卡牌" });
        var scroll = new ScrollContainer { Name = "DeletionScroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _deleteBody.AddChild(scroll);
        var width = Math.Min(1600, GetWindow().Size.X-48);
        var count = Math.Clamp((width-48)/300, 1, 5);
        var grid = new GridContainer { Columns = count }; scroll.AddChild(grid);
        foreach (var card in LastState.GetProperty("own").GetProperty("deck").EnumerateArray())
        {
            var column = new VBoxContainer(); grid.AddChild(column);
            column.AddChild(V12CardPresentation.Face(card, (width-48)/count-12, 580));
            var index = card.GetProperty("index").GetInt32();
            Button(column, "删除此牌", () => _ = SubmitAsync("delete", index: index), !legal.Contains(index));
        }
        Button(_deleteBody, "退出删牌", CloseDeleteWindow);
    }

    private async Task DraftTransition(string intent)
    {
        if (IsBusy || _runtime is null) return;
        await Run(async () =>
        {
            var state = await _runtime.Submit("end_face");
            if (state.GetProperty("status").GetString() == "decision" && state.GetProperty("decision").GetProperty("kind").GetString() == "refresh")
                state = await _runtime.Submit(intent == "refresh" ? "refresh" : "keep_face");
            return state;
        });
    }

    private static string StatusLabel(string key) => key switch
    {
        "atk_down" => "减攻", "def_down" => "减防", "fear" => "恐惧", "heal_block" or "heal_block_max" => "减疗",
        "negative_guard" => "负面防护", "bonus" => "下发增伤%", "delay" => "发射间隔修正", "temp_attack" => "临时攻击",
        "main_def_pen" => "防御穿透%", "main_shield_pen" => "护盾穿透%", "main_attack" => "主弹临时攻击", _ => key
    };

    private static bool IsMissile(JsonElement e) => e.GetProperty("kind").GetString() == "damage" &&
        e.GetProperty("damage_kind").GetString() is "missile" or "true" or "true_missile";

    private void OpenStatus(JsonElement combat)
    {
        var window = new Window { Title = "双方当前状态", Transient = true, Exclusive = true, Unresizable = true };
        AddChild(window);
        var body = new VBoxContainer(); window.AddChild(body); body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        body.OffsetLeft = 24; body.OffsetRight = -24; body.OffsetTop = 20; body.OffsetBottom = -20;
        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(columns);
        foreach (var s in combat.GetProperty("public").EnumerateArray())
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; columns.AddChild(column);
            column.AddChild(new Label { Text = s.GetProperty("seat").GetInt32() == combat.GetProperty("human_seat").GetInt32() ? "你的状态" : "对手状态", Theme = new Theme { DefaultFontSize = 28 } });
            var text = $"生命 {s.GetProperty("hp")}   护盾 {s.GetProperty("shield")}\n攻击 {s.GetProperty("attack")}   防御 {s.GetProperty("defense")}\n当前能量 {s.GetProperty("energy")}\n附魔：{LayerText(s.GetProperty("layers"))}\n余烬 {s.GetProperty("ember")}   潮汐 {s.GetProperty("tide")}\n目标毒层 {s.GetProperty("poison")}\n下一次发射行动值 {s.GetProperty("next_fire")}\n已激活能力：\n{string.Join("、", s.GetProperty("active").EnumerateArray().Select(x => V12CardPresentation.Name(x.GetString())))}";
            text += "\n持续效果：" + string.Join("、", s.GetProperty("modifiers").EnumerateArray().Select(m => StatusLabel(m.GetProperty("kind").GetString()!) + " " + m.GetProperty("value")));
            text += "\n飞弹修正：" + string.Join("、", s.GetProperty("missile_bonuses").EnumerateObject().Select(m => StatusLabel(m.Name) + " " + m.Value));
            column.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, Theme = new Theme { DefaultFontSize = 22 } });
        }
        var close = new Button { Text = "关闭状态" }; close.Pressed += window.QueueFree; body.AddChild(close); window.CloseRequested += window.QueueFree;
        var size = GetWindow().Size; window.PopupCentered(new Vector2I(Math.Min(1100, size.X - 60), Math.Min(720, size.Y - 60)));
    }

    private static string EventText(JsonElement e, JsonElement a, JsonElement b)
    {
        var side = LogPlayerName(e.GetProperty("seat").GetInt32() == 0 ? a : b);
        var prefix = $"[{e.GetProperty("time")}] {side}";
        return e.GetProperty("kind").GetString() switch {
            "play" => $"{prefix} 打出 {V12CardPresentation.Name(e.GetProperty("name").GetString())}",
            "damage" => $"{prefix} 造成 {e.GetProperty("amount")} 伤害（生命 {e.GetProperty("life")}，护盾 {e.GetProperty("shield")}）",
            "heal" => $"{prefix} 回复 {e.GetProperty("amount")} 生命",
            "shield" => $"{prefix} 护盾变为 {e.GetProperty("ordinary")}，临时护盾 {e.GetProperty("temporary")}",
            "poison" => $"{prefix} 毒层 {e.GetProperty("before")} → {e.GetProperty("after")}",
            "revive" => $"{prefix} 复活", "auto_execute" => $"{prefix} 发动主动碎盾", "shuffle" => $"{prefix} 重洗牌组", _ => prefix };
    }

    private static string LogPlayerName(JsonElement id) => id.ValueKind == JsonValueKind.String ? (id.GetString() == "g0" ? "玩家（幽灵）" : "AI " + id.GetString()![1..] + "（幽灵）") : id.GetInt32() == 0 ? "玩家" : "AI " + id.GetInt32();
    private static string CardText(JsonElement card) => $"{V12CardPresentation.Name(card.GetProperty("name").GetString())} · {Element(card.GetProperty("element").GetString())} · {Rarity(card.GetProperty("rarity").GetString())} · {card.GetProperty("cost")}费 · 攻{card.GetProperty("attack")}/防{card.GetProperty("defense")}/首次层{card.GetProperty("layers")}\n{V12CardPresentation.Rules(card)}";
    private static string LayerText(JsonElement layers) => string.Join(" / ", layers.EnumerateObject().Select(x => Element(x.Name) + x.Value));
    private static string PlayerName(JsonElement id) => id.ValueKind == JsonValueKind.String ? "幽灵 " + id.GetString()![1..] : id.GetInt32() == 0 ? "你" : "AI " + id.GetInt32();
    private static string Element(string? value) => value switch { "fire" => "火", "water" => "水", "earth" => "土", "wind" => "风", "poison" => "毒", "generic" => "通用", _ => "未专精" };
    private static string Rarity(string? value) => value switch { "common" => "普通", "rare" => "稀有", "epic" => "史诗", "legendary" => "传奇", "specialization" => "专精", _ => "" };
    private static string Purpose(string? value) => value switch { "discard" => "弃置", "topdeck" => "置顶", "draw" => "检索", "recover" => "回收", _ => "选择卡牌" };
    private void Button(Node parent, string text, Action action, bool disabled = false)
    {
        var button = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill, Text = text, Disabled = disabled, Alignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        button.Pressed += () => { if (!IsBusy) action(); }; parent.AddChild(button); _buttons.Add(button);
    }
    private static void Clear(Node parent) { foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); } }
    private void Restart() { CloseDeleteWindow(); _logs.Visible = false; _runtime?.Dispose(); _runtime = null; LastState = default; LastError = null; _title.Text = "Magicians! · v1.2 单人赛事"; _log.Text = ""; _standings.Text = ""; _ = StartRandomMatchAsync(); }
    private void OpenDisplaySettings()
    {
        var dialog = new DisplaySettingsDialog(); AddChild(dialog);
        dialog.CloseRequested += dialog.QueueFree;
        dialog.PopupCentered(new Vector2I(560, 300));
    }
    private void Back() => GetTree().ChangeSceneToFile("res://Scenes/Boot/Boot.tscn");
}
