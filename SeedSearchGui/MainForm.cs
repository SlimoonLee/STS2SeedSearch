using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SeedSearch;

namespace SeedSearchGui;

public sealed class MainForm : Form
{
    readonly ComboBox _character = Combo(), _act = Combo(), _mode = Combo(), _choice = Combo(), _mapMode = Combo();
    readonly NumericUpDown _asc = Number(0, 10, 10), _fights = Number(0, 15, 2), _unknowns = Number(0, 15, 0),
        _count = Number(1, int.MaxValue, 100000), _rng = Number(0, ulong.MaxValue, 20261008), _checkpointInterval = Number(1, 60, 2),
        _offset = Number(0, SeedUtil.SeedSpace - 1, 0), _threads = Number(0, 1024, 0), _lastFloor = Number(1, 15, 10), _strongPrefix = Number(0, 12, 0);
    readonly CheckBox _unlocked = new() { Text = "暗港已解锁", Checked = true, AutoSize = true },
        _discovered = new() { Text = "暗港已发现", Checked = true, AutoSize = true },
        _fromUnknown = new() { Text = "从首个问号开始检查战斗奖励", Checked = true, AutoSize = true },
        _untilFound = new() { Text = "找到一个为止（不限制数量）", AutoSize = true },
        _continueLast = new() { Text = "从上次继续", AutoSize = true },
        _resume = new() { Text = "自动续跑（检查点）", Checked = true, AutoSize = true },
        _idleSearch = new() { Text = "闲时搜索（有其他负载时自动让步）", AutoSize = true },
        _idleRuntime = new() { Text = "闲时模式（运行中可切换）", AutoSize = true, Visible = false },
        _eliteBlock = new() { Text = "精英堵路（所有路线都必须经过精英）", AutoSize = true },
        _noRestBeforeElite = new() { Text = "第一个精英前禁止火堆", AutoSize = true };
    readonly TextBox _input = new() { Dock = DockStyle.Fill, PlaceholderText = "留空则枚举新种子；支持 txt / jsonl" },
        _output = new() { Dock = DockStyle.Fill };
    readonly SelectionPanel _positives = new(), _curses = new(), _bosses = new(), _cards = new(true), _relics = new(), _strongs = new(), _firstElites = new();
    readonly Button _start = Button("开始搜索"), _stop = Button("停止"), _copy = Button("复制种子"), _open = Button("打开结果文件");
    readonly Label _summary = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.FromArgb(50, 75, 105), Padding = new Padding(8) },
        _status = new() { AutoSize = true, Text = "就绪", Margin = new Padding(12, 8, 0, 0) },
        _stats = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 2, 8, 4) };
    readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 8 };
    readonly ListView _results = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };
    readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    readonly TableLayoutPanel _settings = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
    readonly TabControl _filters = new() { Dock = DockStyle.Fill };
    readonly TabControl _configuration = new() { Dock = DockStyle.Fill };
    readonly FlowLayoutPanel _tools = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(5, 4, 5, 0) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    readonly ConcurrentQueue<Hit> _pending = new();
    readonly Dictionary<string, string[]> _cardMemory = new(StringComparer.Ordinal);
    readonly Dictionary<string, string[]> _relicMemory = new(StringComparer.Ordinal);
    readonly HashSet<string> _bossMemory = new(StringComparer.Ordinal);
    CancellationTokenSource _cts;
    SearchRuntimeControl _runtime;
    Task<SearchResult> _task;
    SearchProgress _latest;
    string _cardCharacter = "", _relicCharacter = "", _lastOutput = "", _automaticOutput, _continuationOutput;
    int _queued;
    bool _loading = true, _closeRequested, _changingContinuation;
    const int DisplayLimit = 1000;
    static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeedSearch", "ui.json");

    public MainForm(bool restorePreferences = true)
    {
        Text = "STS2 种子搜索"; Font = new Font("Microsoft YaHei UI", 9.5F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1380, 880); MinimumSize = new Size(1040, 700);
        StartPosition = FormStartPosition.CenterScreen;
        foreach (var character in Loc.Characters) _character.Items.Add(character.Cn);
        foreach (var act in Loc.Acts) _act.Items.Add(act.Cn);
        _mode.Items.AddRange(new[] { "全部路线：前 X 个房间必须战斗", "存在路线：找到前 X 场合格奖励" });
        _choice.Items.AddRange(new[] { "不应用选项效果", "选择第 1 项（正面）", "选择第 2 项（正面）", "选择第 3 项（诅咒）" });
        _mapMode.Items.AddRange(new[] { "搜索简图（精确）", "完整地图（对照）" });
        _character.SelectedIndex = 1; _act.SelectedIndex = 0; _mode.SelectedIndex = 0; _choice.SelectedIndex = 0; _mapMode.SelectedIndex = 0;
        _stop.Enabled = _copy.Enabled = _open.Enabled = false;
        BuildUi();
        _positives.SetItems(GameData.NeowPositive.Where(r => r != "MassiveScroll").Select(r => new SelectionOption(r, Loc.Relic(r), "正面遗物"))
            .Concat(new[] { "LavaRock", "SmallCapsule", "NutritiousOyster", "StoneHumidifier", "NeowsTalisman", "Pomander" }
                .Select(r => new SelectionOption(r, Loc.Relic(r), "成对遗物"))));
        _curses.SetItems(GameData.NeowCurse.Select(r => new SelectionOption(r, Loc.Relic(r), "诅咒")));
        PopulateCards(); PopulateRelics(); PopulateBosses(); PopulateEncounters(); NewOutput();
        WireEvents(); _loading = false; Summary();
        if (restorePreferences && File.Exists(PreferencesPath))
        {
            try
            {
                var previous = SearchConfig.Load(PreferencesPath);
                ApplyConfig(previous);
                if (OfferContinuation(previous, replaceWhenMissing: true)) _status.Text = "发现可续搜记录，已选中“从上次继续”";
                else _status.Text = "已恢复上次筛选条件";
            }
            catch (Exception e) { _status.Text = "上次配置未恢复：" + e.Message; }
        }
    }

    static ComboBox Combo() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    static NumericUpDown Number(decimal min, decimal max, decimal value) => new()
    { Minimum = min, Maximum = max, Value = value, Dock = DockStyle.Fill, ThousandsSeparator = true };
    static Button Button(string text) => new() { Text = text, AutoSize = true, Height = 34, Margin = new Padding(4, 2, 4, 2) };
    static Label Note(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Padding = new Padding(3, 4, 3, 4), MaximumSize = new Size(390, 0) };

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 63)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 10)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 37)); Controls.Add(root);
        var title = new Label { Text = "STS2 种子搜索   ·   离线重放 / 单人 / 进阶 0–10", AutoSize = true,
            Font = new Font(Font.FontFamily, 15, FontStyle.Bold), Padding = new Padding(6, 3, 6, 6) };
        _tools.Controls.Add(title);
        Tool("载入配置", LoadConfig); Tool("保存配置", SaveConfig);
        Tool("仅筛开局", () => { _fights.Value = 0; _unknowns.Value = 0; Summary(); });
        Tool("前10层", () => { _lastFloor.Value = 10; _mapMode.SelectedIndex = 0; Summary(); });
        Tool("默认条件", () => ApplyConfig(new SearchConfig { minFightsChecked = 2, maxSearchFloor = 10 }));
        root.Controls.Add(_tools, 0, 0); root.Controls.Add(_summary, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 7, Size = new Size(1200, 500) };
        split.Panel1MinSize = 330; split.Panel2MinSize = 550; split.SplitterDistance = 380;
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 8, 0) };
        _settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); scroller.Controls.Add(_settings);
        var conditionPage = new TabPage("条件"); conditionPage.Controls.Add(scroller); _configuration.TabPages.Add(conditionPage);
        split.Panel1.Controls.Add(_configuration);
        _settings.Controls.Add(SettingsGroup("基础", new[] {
            ("职业", (Control)_character), ("进阶", _asc), ("第一幕", _act), ("账号状态", Row(_unlocked, _discovered)) }));
        _settings.Controls.Add(SettingsGroup("路线与奖励", new[] {
            ("路线模式", (Control)_mode), ("检查战斗数 X", _fights), ("前 K 个 ? 出怪", _unknowns),
            ("", _fromUnknown), ("", Note("卡牌白名单检查每场三张候选及实际选择的捏奥额外卡牌。X=0、K=0 跳过地图，仍检查捏奥卡牌。固定诅咒不检查。")) }));
        var rangePage = new TabPage("范围与文件");
        var rangeScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        rangeScroll.Controls.Add(SettingsGroup("搜索范围与文件", new[] {
            ("", (Control)_untilFound), ("", (Control)_continueLast), ("", (Control)_resume), ("", (Control)_idleSearch), ("数量", _count), ("随机键", _rng), ("起始索引", _offset), ("线程", _threads), ("保存间隔(秒)", _checkpointInterval),
            ("输入名单", BrowseRow(_input, false)), ("输出 JSONL", BrowseRow(_output, true)),
            ("", Note("找到为止：忽略数量，按当前条件持续搜索，命中一个后保存并自动停止，也可手动停止。名单模式找到即停；未命中则处理到名单末尾。")),
            ("", Note("闲时搜索：搜索线程使用低优先级，并根据其他应用占用的 CPU 动态减小并行度和工作时间；负载下降后自动恢复。运行中可用下方“闲时模式”开关热切换；监测不可用时保守限制为单线程约20%工作比例。")),
            ("", Note("线程 0=自动。随机范围会保存带校验和的 .checkpoint.json 及 .bak；断电后只重扫最近未提交块，并直接跳过已完成范围。结果旁还会保留 .errors.jsonl 排错记录；名单模式不使用检查点。")) }));
        rangePage.Controls.Add(rangeScroll); _configuration.TabPages.Add(rangePage);
        var mapPage = new TabPage("地图范围");
        var mapScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        mapScroll.Controls.Add(SettingsGroup("第一幕搜索范围", new[] {
            ("地图模式", (Control)_mapMode), ("搜索截止层", _lastFloor),
            ("", Note("第1层是开局战斗；截止层与检查战斗数 X 分开设置。前10层表示条件最迟在第10层满足。")),
            ("", Note("搜索简图保留房间和连边，落选种子省去坐标整理。完整地图使用相同条件，可用于对照。")),
            ("", Note("Boss 和遭遇队列在建图前筛选。“仅筛开局”检查 Boss、遭遇队列、捏奥及已应用的捏奥卡牌；精英堵路和第一个精英前禁火仍需建图判断。")) }));
        mapPage.Controls.Add(mapScroll); _configuration.TabPages.Add(mapPage);
        var neow = new TabPage("捏奥白名单");
        var neowLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        neowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); neowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        neowLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); neowLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        neowLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        neowLayout.Controls.Add(Note("两个正面选项均须在左侧白名单内；诅咒须在右侧。空选不限。实际选择后，全部随机卡牌候选参与卡牌白名单检查；固定诅咒不检查。"), 0, 0);
        neowLayout.SetColumnSpan(neowLayout.GetControlFromPosition(0, 0), 2);
        neowLayout.Controls.Add(_positives, 0, 1); neowLayout.Controls.Add(_curses, 1, 1);
        neowLayout.Controls.Add(Note("实际选择（影响奖励重放）"), 0, 2); neowLayout.Controls.Add(_choice, 1, 2);
        neow.Controls.Add(neowLayout); _filters.TabPages.Add(neow);
        AddTab("卡牌白名单", _cards); AddTab("Boss 白名单", _bosses);
        var relicPage = new TabPage("遗物白名单");
        var relicLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(6) };
        relicLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); relicLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        relicLayout.Controls.Add(Note("只检查遗物序列实际取出的遗物：选中的捏奥遗物本身不计入；选择小型扭蛋或巨大扭蛋时，只检查它们从遗物袋取出的遗物，以及路线上的首个精英遗物。空=不限。"), 0, 0);
        relicLayout.Controls.Add(_relics, 0, 1); relicPage.Controls.Add(relicLayout); _filters.TabPages.Add(relicPage);
        var encounters = new TabPage("遭遇条件") { AutoScroll = true };
        var encounterLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 4, Padding = new Padding(6) };
        encounterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); encounterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        encounterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); encounterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var encounterSettings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _strongPrefix.Width = 82;
        encounterSettings.Controls.Add(new Label { Text = "强怪池前 N 个", AutoSize = true, Padding = new Padding(0, 7, 2, 0) });
        encounterSettings.Controls.Add(_strongPrefix);
        encounterSettings.Controls.Add(new Label { Text = "（0=按白名单数量；空组合=不限）", AutoSize = true, Padding = new Padding(2, 7, 10, 0) });
        encounterSettings.Controls.Add(_eliteBlock);
        encounterSettings.Controls.Add(_noRestBeforeElite);
        encounterLayout.Controls.Add(encounterSettings, 0, 0);
        encounterLayout.Controls.Add(Note("以下选项全部从游戏配置自动读取，直接勾选即可，无需手动输入。强怪白名单中的组合可以多于 N 个；弱怪之后的前 N 个强怪每一项都必须属于所选白名单。第一个精英从精英队列第一项匹配。每项名称对应游戏中的整组怪物遭遇。"), 0, 1);
        var strongGroup = new GroupBox { Text = "强怪白名单（游戏配置，前 N 项均须属于所选组合）", Dock = DockStyle.Fill, Height = 250, MinimumSize = new Size(0, 250), Padding = new Padding(3) }; strongGroup.Controls.Add(_strongs);
        var eliteGroup = new GroupBox { Text = "第一个精英可选组合（游戏配置，空=不限）", Dock = DockStyle.Fill, Height = 250, MinimumSize = new Size(0, 250), Padding = new Padding(3) }; eliteGroup.Controls.Add(_firstElites);
        encounterLayout.Controls.Add(strongGroup, 0, 2); encounterLayout.Controls.Add(eliteGroup, 0, 3);
        encounters.Controls.Add(encounterLayout); _filters.TabPages.Add(encounters);
        split.Panel2.Controls.Add(_filters); root.Controls.Add(split, 0, 2);
        var run = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        run.Controls.AddRange(new Control[] { _start, _stop, _copy, _open, _idleRuntime, _status }); root.Controls.Add(run, 0, 3);
        root.Controls.Add(_progress, 0, 4); root.Controls.Add(_stats, 0, 5);
        var resultSplit = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1200, 280), SplitterDistance = 830, SplitterWidth = 7 };
        foreach (var (name, width) in new[] { ("种子", 150), ("第一幕", 60), ("Boss", 105), ("捏奥 A", 125), ("捏奥 B", 125), ("诅咒", 115), ("已检查战斗", 95) })
            _results.Columns.Add(name, width);
        resultSplit.Panel1.Controls.Add(_results); resultSplit.Panel2.Controls.Add(_detail); root.Controls.Add(resultSplit, 0, 6);
        _detail.Text = "选中结果查看捏奥卡牌、路线、问号掷点与每场奖励。\r\n界面显示前 1,000 个命中，全量结果保存在 JSONL 文件中。";

        void Tool(string text, Action click) { var b = Button(text); b.Click += (_, _) => click(); _tools.Controls.Add(b); }
        void AddTab(string text, Control content) { var page = new TabPage(text); page.Controls.Add(content); _filters.TabPages.Add(page); }
    }

    static Control Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        row.Controls.AddRange(controls); return row;
    }
    GroupBox SettingsGroup(string title, IEnumerable<(string label, Control control)> rows)
    {
        var group = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;
        foreach (var (label, control) in rows)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(2, 5, 2, 5);
            if (label.Length == 0) { table.Controls.Add(control, 0, row); table.SetColumnSpan(control, 2); }
            else
            {
                table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 2, 6) }, 0, row);
                table.Controls.Add(control, 1, row);
            }
            row++;
        }
        group.Controls.Add(table); return group;
    }
    Control BrowseRow(TextBox text, bool save)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var button = Button("…"); button.Width = 32;
        button.Click += (_, _) =>
        {
            using FileDialog dialog = save ? new SaveFileDialog { Filter = "JSON Lines|*.jsonl", FileName = Path.GetFileName(text.Text) }
                : new OpenFileDialog { Filter = "种子名单|*.jsonl;*.txt|全部文件|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK) text.Text = dialog.FileName;
        };
        row.Controls.Add(text, 0, 0); row.Controls.Add(button, 1, 0); return row;
    }

    void WireEvents()
    {
        _character.SelectedIndexChanged += (_, _) => { if (!_loading) { InvalidateContinuation(); PopulateCards(); PopulateRelics(); Summary(); } };
        _act.SelectedIndexChanged += (_, _) => { if (!_loading) { InvalidateContinuation(); PopulateBosses(); PopulateEncounters(); Summary(); } };
        _mode.SelectedIndexChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        _mapMode.SelectedIndexChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        _untilFound.CheckedChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        _resume.CheckedChanged += (_, _) => Summary();
        _idleSearch.CheckedChanged += (_, _) =>
        {
            if (_task is { IsCompleted: false } && _runtime != null && _idleRuntime.Checked != _idleSearch.Checked)
                _runtime.SetIdleSearch(_idleSearch.Checked);
            Summary();
        };
        _idleRuntime.CheckedChanged += (_, _) =>
        {
            if (_task is { IsCompleted: false } && _runtime != null)
            {
                _runtime.SetIdleSearch(_idleRuntime.Checked);
                if (_idleSearch.Checked != _idleRuntime.Checked) _idleSearch.Checked = _idleRuntime.Checked;
            }
            Summary();
        };
        _continueLast.CheckedChanged += (_, _) => ContinuationChanged();
        _unlocked.CheckedChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        _discovered.CheckedChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        foreach (var list in new[] { _positives, _curses, _bosses, _cards, _relics, _strongs, _firstElites }) list.SelectionChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        foreach (var number in new[] { _asc, _fights, _unknowns, _lastFloor, _strongPrefix, _count, _rng, _offset }) number.ValueChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        _eliteBlock.CheckedChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        _noRestBeforeElite.CheckedChanged += (_, _) => { InvalidateContinuation(); Summary(); };
        _start.Click += async (_, _) => await StartSearch();
        _stop.Click += (_, _) => { _stop.Enabled = false; _status.Text = "正在停止并保存结果…"; _cts?.Cancel(); };
        _copy.Click += (_, _) => { if (_results.SelectedItems.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, _results.SelectedItems.Cast<ListViewItem>().Select(item => ((Hit)item.Tag).seed))); };
        _open.Text = "结果目录";
        _open.Click += (_, _) => { if (File.Exists(_lastOutput)) Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_lastOutput)) { UseShellExecute = true }); };
        _results.SelectedIndexChanged += (_, _) => ShowDetail();
        _results.DoubleClick += (_, _) => _copy.PerformClick();
        _timer.Tick += (_, _) => Drain();
        FormClosing += (_, e) =>
        {
            if (_task is { IsCompleted: false }) { e.Cancel = true; _closeRequested = true; _cts?.Cancel(); return; }
            try { Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)); ReadConfig().Save(PreferencesPath); } catch (Exception) { }
        };
        FormClosed += (_, _) => _timer.Dispose();
    }
    string Character => Loc.Characters[Math.Max(0, _character.SelectedIndex)].Id;
    void PopulateCards()
    {
        if (_cardCharacter.Length > 0) _cardMemory[_cardCharacter] = _cards.CheckedIds();
        _cardCharacter = Character;
        string[] selected = _cardMemory.GetValueOrDefault(Character) ?? [];
        int Order(string rarity) => rarity switch { "Common" => 0, "Uncommon" => 1, "Rare" => 2, _ => 3 };
        var primary = GameData.CardPools[Character + "CardPool"].ToHashSet(StringComparer.Ordinal);
        string Source(string card) => primary.Contains(card) ? "当前职业" : GameData.CardPools["ColorlessCardPool"].Contains(card) ? "无色"
            : Loc.Characters.First(c => GameData.CardPools[c.Id + "CardPool"].Contains(card)).Cn;
        _cards.SetItems(Game.FilterableCards(Character).OrderBy(c => primary.Contains(c) ? 0 : Source(c) == "无色" ? 1 : 2)
            .ThenBy(Source, StringComparer.Ordinal).ThenBy(c => Order(GameData.CardRarity[c])).ThenBy(Loc.Card, StringComparer.Ordinal)
            .Select(c => new SelectionOption(c, Loc.Card(c), Loc.RarityCn(GameData.CardRarity[c]), Source(c), primary.Contains(c))), selected);
    }
    void PopulateRelics()
    {
        if (_relicCharacter.Length > 0) _relicMemory[_relicCharacter] = _relics.CheckedIds();
        string character = Character;
        string[] selected = _relicMemory.GetValueOrDefault(character) ?? [];
        var primary = GameData.RelicPools.GetValueOrDefault(character + "RelicPool", [])!.ToHashSet(StringComparer.Ordinal);
        int Order(string rarity) => rarity switch { "Common" => 0, "Uncommon" => 1, "Rare" => 2, "Shop" => 3, _ => 4 };
        _relics.SetItems(RelicBag.SequenceRelicIds(character)
            .OrderBy(r => primary.Contains(r) ? 0 : 1)
            .ThenBy(r => Order(GameData.RelicRarity[r]))
            .ThenBy(Loc.Relic, StringComparer.Ordinal)
            .Select(r => new SelectionOption(r, Loc.Relic(r), Loc.RarityCn(GameData.RelicRarity[r]), primary.Contains(r) ? "本职业" : "共享", primary.Contains(r))), selected);
        _relicCharacter = character;
        _relicMemory[character] = _relics.CheckedIds();
    }
    void PopulateBosses()
    {
        foreach (string id in _bosses.OptionIds) _bossMemory.Remove(id);
        foreach (string id in _bosses.CheckedIds()) _bossMemory.Add(id);
        string act = Loc.Acts[Math.Max(0, _act.SelectedIndex)].Id;
        _bosses.SetItems(GameData.Acts.Where(a => a.Index == 0 && (act.Length == 0 || a.Name == act))
            .SelectMany(a => a.Bosses.Select(b => new SelectionOption(Game.Snake(b.Name), Loc.Encounter(Game.Snake(b.Name)), a.Name == "Overgrowth" ? "密林" : "暗港"))), _bossMemory);
    }
    void PopulateEncounters()
    {
        string act = Loc.Acts[Math.Max(0, _act.SelectedIndex)].Id;
        var acts = GameData.Acts.Where(a => a.Index == 0 && (act.Length == 0 || a.Name == act)).ToArray();
        string[] strong = _strongs.CheckedIds(), elites = _firstElites.CheckedIds();
        string Display(EncDef encounter)
        {
            string group = Loc.Encounter(Game.Snake(encounter.Name));
            string members = encounter.Monsters is { Length: > 0 }
                ? string.Join("、", encounter.Monsters.Select(m => Loc.Monster(Game.Snake(m)))) : "成员未知";
            return $"{group}（{members}）";
        }
        _strongs.SetItems(acts.SelectMany(a => a.Regular.Select(e => new SelectionOption(e.Name, Display(e), "强怪池", a.Name))), strong);
        _firstElites.SetItems(acts.SelectMany(a => a.Elites.Select(e => new SelectionOption(e.Name, Display(e), "精英池", a.Name))), elites);
    }
    void Summary()
    {
        if (_loading) return;
        _fromUnknown.Enabled = _mode.SelectedIndex == 1;
        _discovered.Enabled = _unlocked.Checked;
        _count.Enabled = !_untilFound.Checked;
        _summary.Text = $"{Loc.Characters[_character.SelectedIndex].Cn} · A{_asc.Value} · {Loc.Acts[_act.SelectedIndex].Cn} · Boss {_bosses.CheckedIds().Length} 项 · 强怪前 {_strongPrefix.Value} / 白名单 {_strongs.CheckedIds().Length} 项 · 首精英 {_firstElites.CheckedIds().Length} 项 · 堵路 {_eliteBlock.Checked} · 禁火 {_noRestBeforeElite.Checked} · 捏奥 {_positives.CheckedIds().Length}+{_curses.CheckedIds().Length} 项 · 卡牌 {_cards.CheckedIds().Length} 项 · 遗物序列 {_relics.CheckedIds().Length} 项 · 闲时 {_idleSearch.Checked}   |   "
            + (_fights.Value == 0 && _unknowns.Value == 0 ? "仅筛开局（不建图）" : $"前 {_lastFloor.Value} 层 · {(_mapMode.SelectedIndex == 0 ? "简图" : "全图")} · {(_mode.SelectedIndex == 0 ? "全部路线" : "存在路线")} · 检查 {_fights.Value} 场 · 前 {_unknowns.Value} 个 ? 出怪")
            + (_untilFound.Checked ? " · 找到一个即停" : "") + (_continueLast.Checked ? " · 从上次继续" : _resume.Checked ? " · 自动续跑" : "");
        if (_latest != null && _task is { IsCompleted: true }) _status.Text = "条件已修改 · 当前显示上次搜索结果";
    }
    internal SearchConfig ReadConfig() => new SearchConfig
    {
        character = Character, ascension = (int)_asc.Value, act1 = Loc.Acts[_act.SelectedIndex].Id,
        underdocksUnlocked = _unlocked.Checked, underdocksDiscovered = _discovered.Checked,
        bossAllow = _bosses.CheckedIds(), normalStrongPrefixLength = (int)_strongPrefix.Value, normalStrongAllow = _strongs.CheckedIds(),
        firstEliteAllow = _firstElites.CheckedIds(), eliteBlock = _eliteBlock.Checked, noRestBeforeFirstElite = _noRestBeforeElite.Checked,
        neowPositives = _positives.CheckedIds(), neowCurses = _curses.CheckedIds(),
        neowChoice = _choice.SelectedIndex - 1, routeMode = _mode.SelectedIndex == 0 ? "all" : "exists",
        minFightsChecked = (int)_fights.Value, unknownMonsterStreak = (int)_unknowns.Value, checkFromFirstUnknown = _fromUnknown.Checked,
        mapMode = _mapMode.SelectedIndex == 0 ? "search" : "full", maxSearchFloor = (int)_lastFloor.Value,
        allowedCards = _cards.CheckedIds(), allowedRelics = _relics.CheckedIds(), findUntilHit = _untilFound.Checked, resumeFromCheckpoint = _resume.Checked, idleSearch = _idleSearch.Checked, count = (int)_count.Value, rngSeed = (ulong)_rng.Value, startIndex = (ulong)_offset.Value,
        threads = (int)_threads.Value, checkpointIntervalSeconds = (int)_checkpointInterval.Value, outFile = _output.Text, seedListFile = _input.Text
    }.ValidatedCopy();

    internal void ApplyConfig(SearchConfig config)
    {
        var cfg = config.ValidatedCopy(); _loading = true;
        try
        {
            _continuationOutput = null; _continueLast.Checked = false; _continueLast.Enabled = false;
            _cardCharacter = ""; _relicCharacter = ""; _cardMemory.Clear(); _relicMemory.Clear(); _bossMemory.Clear();
            _character.SelectedIndex = Array.FindIndex(Loc.Characters, c => c.Id == cfg.character);
            _act.SelectedIndex = Array.FindIndex(Loc.Acts, a => a.Id == cfg.act1);
            _asc.Value = cfg.ascension; _unlocked.Checked = cfg.underdocksUnlocked; _discovered.Checked = cfg.underdocksDiscovered;
            _mode.SelectedIndex = cfg.routeMode == "all" ? 0 : 1; _choice.SelectedIndex = cfg.neowChoice + 1;
            _fights.Value = cfg.minFightsChecked; _unknowns.Value = cfg.unknownMonsterStreak; _fromUnknown.Checked = cfg.checkFromFirstUnknown;
            _mapMode.SelectedIndex = cfg.mapMode == "search" ? 0 : 1; _lastFloor.Value = cfg.maxSearchFloor;
            _untilFound.Checked = cfg.findUntilHit; _resume.Checked = cfg.resumeFromCheckpoint; _idleSearch.Checked = cfg.idleSearch; _count.Value = Math.Clamp(cfg.count, 1, int.MaxValue);
            _rng.Value = cfg.rngSeed; _offset.Value = cfg.startIndex; _threads.Value = cfg.threads; _checkpointInterval.Value = cfg.checkpointIntervalSeconds;
            _input.Text = cfg.seedListFile; _output.Text = cfg.outFile;
            PopulateCards(); _cards.SelectIds(cfg.allowedCards); PopulateRelics(); _relics.SelectIds(cfg.allowedRelics); PopulateBosses(); _bosses.SelectIds(cfg.bossAllow); PopulateEncounters();
            _strongPrefix.Value = cfg.normalStrongPrefixLength; _strongs.SelectIds(cfg.normalStrongAllow); _firstElites.SelectIds(cfg.firstEliteAllow); _eliteBlock.Checked = cfg.eliteBlock; _noRestBeforeElite.Checked = cfg.noRestBeforeFirstElite;
            _positives.SelectIds(cfg.neowPositives); _curses.SelectIds(cfg.neowCurses);
        }
        finally { _loading = false; Summary(); }
    }
    bool OfferContinuation(SearchConfig config, bool replaceWhenMissing = false)
    {
        bool found = SearchEngine.HasCompatibleCheckpoint(config);
        _changingContinuation = true;
        try
        {
            _continuationOutput = found ? Path.GetFullPath(config.outFile) : null;
            _continueLast.Enabled = found;
            _continueLast.Checked = found;
            if (found)
            {
                _output.Text = _continuationOutput;
                _resume.Checked = true;
            }
            else if (replaceWhenMissing) NewOutput();
        }
        finally { _changingContinuation = false; }
        Summary();
        return found;
    }
    void ContinuationChanged()
    {
        if (_loading || _changingContinuation) return;
        _changingContinuation = true;
        try
        {
            if (_continueLast.Checked && !string.IsNullOrWhiteSpace(_continuationOutput))
            {
                _output.Text = _continuationOutput;
                _resume.Checked = true;
            }
            else
            {
                _continuationOutput = null;
                _continueLast.Enabled = false;
                NewOutput();
            }
        }
        finally { _changingContinuation = false; }
        Summary();
    }
    void InvalidateContinuation()
    {
        if (_loading || _changingContinuation || !_continueLast.Checked) return;
        _changingContinuation = true;
        try { NewOutput(); }
        finally { _changingContinuation = false; }
    }
    void NewOutput()
    {
        var now = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "China Standard Time");
        _automaticOutput = Path.GetFullPath(Path.Combine("results", $"hits-{now:yyyyMMdd-HHmmss-fff}.jsonl"));
        _continuationOutput = null;
        _continueLast.Checked = false;
        _continueLast.Enabled = false;
        _output.Text = _automaticOutput;
    }
    void LoadConfig()
    {
        using var dialog = new OpenFileDialog { Filter = "搜索配置|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var config = SearchConfig.Load(dialog.FileName);
            ApplyConfig(config);
            _status.Text = OfferContinuation(config) ? "已载入配置，并发现可续搜记录" : "已载入：" + Path.GetFileName(dialog.FileName);
        }
        catch (Exception e) { Error(e); }
    }
    void SaveConfig()
    {
        try
        {
            var cfg = ReadConfig();
            using var dialog = new SaveFileDialog { Filter = "搜索配置|*.json", FileName = "search-config.json" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { cfg.Save(dialog.FileName); _status.Text = "配置已保存"; }
        }
        catch (Exception e) { Error(e); }
    }

    async Task StartSearch()
    {
        if (_task is { IsCompleted: false }) return;
        SearchConfig cfg;
        try
        {
            if (_continueLast.Checked && !string.IsNullOrWhiteSpace(_continuationOutput))
            {
                _output.Text = _continuationOutput;
                _resume.Checked = true;
            }
            else if (_output.Text == _automaticOutput) NewOutput();
            cfg = ReadConfig();
        }
        catch (Exception e) { Error(e); return; }
        _lastOutput = Path.GetFullPath(cfg.outFile); _latest = null;
        _results.Items.Clear(); _pending.Clear(); _queued = 0;
        _detail.Text = cfg.findUntilHit ? "正在持续搜索…\r\n找到一个符合全部条件的种子后保存并自动停止。" : "正在搜索…\r\n界面最多显示 1,000 个命中，全量结果写入输出文件。";
        _runtime = new SearchRuntimeControl(cfg.idleSearch);
        _idleRuntime.Checked = cfg.idleSearch; _idleRuntime.Visible = true; _idleRuntime.Enabled = true;
        _cts = new(); _start.Enabled = false; _stop.Enabled = true; _open.Enabled = _copy.Enabled = false;
        _configuration.Enabled = _filters.Enabled = _tools.Enabled = false;
        _progress.Value = 0;
        _progress.Style = cfg.findUntilHit || cfg.seedListFile.Length > 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        _status.Text = cfg.findUntilHit ? "持续搜索，找到一个为止…" : "正在搜索…"; _timer.Start();
        _task = Task.Run(() => SearchEngine.Run(cfg, hit =>
        {
            if (Interlocked.Increment(ref _queued) <= DisplayLimit) _pending.Enqueue(hit);
        }, p => Volatile.Write(ref _latest, p), _cts.Token, _runtime));
        try
        {
            var result = await _task; Drain(true);
            _progress.Style = ProgressBarStyle.Continuous;
            if (!result.Canceled) _progress.Value = 1000;
            string outcome = cfg.findUntilHit && result.Progress.Hits > 0 ? "已找到，自动停止" : result.Canceled ? "已停止" : "完成";
            _status.Text = $"{outcome}{(result.Resumed ? "（从检查点续跑" + (result.RecoveredCheckpoint ? "备份" : "") + (result.RecoveredOutput ? "，已回退尾部" : "") + "）" : "")} · 评估 {result.Progress.Evaluated:N0} · 命中 {result.Progress.Hits:N0} · {result.Progress.Elapsed.TotalSeconds:F1}s";
            if (!result.Canceled && (cfg.seedListFile.Length > 0 || string.IsNullOrEmpty(result.CheckpointPath) || !File.Exists(result.CheckpointPath)))
            {
                // A completed range has no checkpoint to continue.  Keep the
                // finished file available through _lastOutput, but prepare a
                // fresh output for the next click instead of reporting a
                // misleading stale-continuation error.
                NewOutput();
            }
            if (result.Progress.Hits == 0)
                _detail.Text = result.Canceled ? "搜索已停止，尚未找到符合条件的种子。"
                    : cfg.seedListFile.Length > 0 ? "输入名单已处理完，未找到符合条件的种子。清空输入名单可直接枚举新种子。"
                    : cfg.findUntilHit ? "当前起始索引之后的种子空间已搜索完，未找到符合条件的种子。"
                    : "本次未命中。可查看过滤统计判断淘汰阶段，增加搜索数量，或减少战斗数、扩大白名单。";
            if (_results.Items.Count > 0) { _results.Items[0].Selected = true; ShowDetail(); }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { Drain(true); _status.Text = "已停止"; }
        catch (Exception e) { Drain(true); Error(e is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : e); }
        finally
        {
            if (_idleSearch.Checked != _idleRuntime.Checked) _idleSearch.Checked = _idleRuntime.Checked;
            _idleRuntime.Visible = false; _idleRuntime.Enabled = false; _runtime = null;
            _timer.Stop(); _progress.Style = ProgressBarStyle.Continuous;
            _stop.Enabled = false; _start.Enabled = _configuration.Enabled = _filters.Enabled = _tools.Enabled = true;
            _open.Enabled = File.Exists(_lastOutput); _cts.Dispose(); _cts = null;
            if (_closeRequested) Close();
        }
    }
    void Drain(bool all = false)
    {
        _results.BeginUpdate();
        try
        {
            int batch = 0;
            while ((all || batch++ < 100) && _results.Items.Count < DisplayLimit && _pending.TryDequeue(out var hit))
            {
                var row = new ListViewItem(new[] { hit.seed, hit.act1 == "Overgrowth" ? "密林" : "暗港", Loc.Encounter(hit.boss),
                    Loc.Relic(hit.neowA), Loc.Relic(hit.neowB), Loc.Relic(hit.neowC), hit.checkedFights.ToString() }) { Tag = hit };
                _results.Items.Add(row);
            }
        }
        finally { _results.EndUpdate(); }
        var progress = Volatile.Read(ref _latest);
        if (progress == null) return;
        _progress.Style = progress.Total.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (progress.Total.HasValue) _progress.Value = (int)Math.Clamp(progress.Evaluated * 1000 / Math.Max(1, progress.Total.Value), 0, 1000);
        _status.Text = $"评估 {progress.Evaluated:N0}{(progress.Total.HasValue ? $"/{progress.Total:N0}" : "")} · 命中 {progress.Hits:N0} · {progress.SeedsPerSec:N0}/s"
            + (progress.Remaining.HasValue ? $" · 剩余约 {progress.Remaining.Value:hh\\:mm\\:ss}" : "");
        _stats.Text = progress.Stages + $"   |   显示 {_results.Items.Count:N0}/{progress.Hits:N0} 命中";
        if (progress.Idle != null) _stats.Text += $"   |   {progress.Idle.Description}";
    }
    void ShowDetail()
    {
        _copy.Enabled = _results.SelectedItems.Count > 0;
        if (!_copy.Enabled) return;
        var hit = (Hit)_results.SelectedItems[0].Tag;
        _detail.Text = FormatDetail(hit);
    }
    static string FormatDetail(Hit hit)
    {
        var text = new StringBuilder($"种子：{hit.seed}\r\n第一幕：{hit.act1}\r\nBoss：{Loc.Encounter(hit.boss)} ({hit.boss})\r\n捏奥：{Loc.Relic(hit.neowA)} / {Loc.Relic(hit.neowB)} / {Loc.Relic(hit.neowC)}\r\n已检查 {hit.checkedFights} 场\r\n");
        if (hit.normalQueue != null)
        {
            int weak = Game.Act(hit.act1).WeakCount;
            text.Append("\r\n普通遭遇队列（弱怪后为强怪池）：\r\n");
            for (int i = 0; i < hit.normalQueue.Length; i++)
                text.Append($"  {i + 1}. {(i < weak ? "弱怪" : "强怪")}：{Loc.Encounter(Game.Snake(hit.normalQueue[i]))} ({hit.normalQueue[i]})\r\n");
        }
        if (hit.eliteQueue != null)
        {
            text.Append("\r\n精英遭遇队列（第 1 项为首精英）：\r\n");
            for (int i = 0; i < hit.eliteQueue.Length; i++)
                text.Append($"  {i + 1}. {Loc.Encounter(Game.Snake(hit.eliteQueue[i]))} ({hit.eliteQueue[i]})\r\n");
        }
        if (hit.neow != null)
        {
            text.Append($"\r\n已应用捏奥：{Loc.Relic(hit.neow.relic)}\r\n");
            foreach (var group in hit.neow.cards.GroupBy(c => (c.kind, c.group)))
            {
                string label = group.Key.kind switch { "offer" => $"候选组 {group.Key.group}（全部检查）", "bundle" => $"卡牌包 {group.Key.group}（全部检查）",
                    "transform" => "变形结果（检查）", "fixedCurse" => "固定诅咒（不筛选）", _ => "新增卡牌（检查）" };
                text.Append(label + "：\r\n");
                foreach (var card in group)
                {
                    string from = card.from == "StartingBasic" ? "选中的起始基础牌" : card.from.Length > 0 ? Loc.Card(card.from) : "";
                    text.Append($"  {(from.Length > 0 ? from + " → " : "")}{Loc.Card(card.card)} ({card.card})\r\n");
                }
            }
            if (hit.neow.potions.Length > 0) text.Append("开局药水：" + string.Join(" / ", hit.neow.potions) + "\r\n");
            if (hit.neow.relics is { Length: > 0 }) text.Append("捏奥效果取出的遗物（序列白名单检查）：" + string.Join(" / ", hit.neow.relics.Select(Loc.Relic)) + "\r\n");
            if (!hit.neow.exact) text.Append("连锁遗物效果尚未完整重放；启用卡牌白名单时此类种子不通过。\r\n");
        }
        if (hit.firstEliteRelic is { Length: > 0 }) text.Append($"\r\n首个精英实际遗物（序列白名单检查）：{Loc.Relic(hit.firstEliteRelic)} ({hit.firstEliteRelic})\r\n");
        for (int i = 0; i < hit.fights.Length; i++)
        {
            var fight = hit.fights[i]; text.Append($"\r\n第 {i + 1} 场 · {Loc.Encounter(fight.encounter)} · {fight.gold} 金币\r\n");
            foreach (string card in fight.cards) text.Append($"  {Loc.Card(card)} ({card})\r\n");
            if (fight.potion.Length > 0) text.Append($"药水：{fight.potion}\r\n");
            if (fight.eliteRelic.Length > 0) text.Append($"遗物：{Loc.Relic(fight.eliteRelic)}\r\n");
        }
        if (hit.treasures.Length > 0)
        {
            text.Append("\r\n宝箱奖励序列（金币 + 遗物）：\r\n");
            for (int i = 0; i < hit.treasures.Length; i++)
            {
                string[] parts = hit.treasures[i].Split('+', 2);
                string relic = parts.Length == 2 ? Loc.Relic(parts[1]) : hit.treasures[i];
                text.Append($"  {i + 1}. {(parts.Length == 2 ? parts[0] + "+" + relic : relic)}\r\n");
            }
        }
        text.Append("\r\n已验证路线前缀：\r\n" + string.Join(" → ", hit.route));
        if (hit.unknownRolls.Length > 0) text.Append("\r\n问号掷点：" + string.Join(" → ", hit.unknownRolls));
        return text.ToString();
    }
    void Error(Exception error)
    {
        _status.Text = "错误：" + error.Message; _detail.Text = error.Message;
        MessageBox.Show(this, error.Message, "搜索配置或执行错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    internal void VerifyCharacterSelectionMemory()
    {
        string colorless = GameData.CardPools["ColorlessCardPool"].First(c => !GameData.CardMultiplayerOnly.Contains(c));
        string[] selected = ["Backflip", "DeadlyPoison", "Anger", colorless];
        _character.SelectedIndex = 1; _cards.SelectIds(selected);
        int count = _cards.OptionCount;
        _character.SelectedIndex = 0; _cards.SelectIds(new[] { "Anger" }); _character.SelectedIndex = 1;
        if (_cards.OptionCount != count || !_cards.CheckedIds().ToHashSet().SetEquals(selected))
            throw new Exception("职业切换丢失选项或重复添加卡牌。");
    }

    internal void VerifyEncounterControls()
    {
        var cfg = ReadConfig();
        if (cfg.normalStrongPrefixLength != 2
            || !cfg.normalStrongAllow.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "CorpseSlugsNormal", "CultistsNormal", "FossilStalkerNormal", "GremlinMercNormal", "HauntedShipNormal" })
            || !cfg.firstEliteAllow.SequenceEqual(new[] { "PhantasmalGardenersElite" })
            || !cfg.eliteBlock || !cfg.noRestBeforeFirstElite
            || _strongPrefix.Value != 2 || !_eliteBlock.Checked || !_noRestBeforeElite.Checked
            || !_strongs.VisibleNames.Any(name => name.Contains("邪教徒") || name.Contains("CalcifiedCultist"))
            || _strongs.VisibleCount == 0 || _firstElites.VisibleCount == 0)
            throw new Exception("遭遇条件控件或配置往返未保留。");
    }

    internal void VerifyRelicControls()
    {
        var ids = _relics.OptionIds.ToHashSet(StringComparer.Ordinal);
        var cfg = ReadConfig();
        if (!ids.Contains("Anchor") || ids.Contains("SmallCapsule") || ids.Contains("LargeCapsule") || ids.Contains("NeowsBones")
            || cfg.allowedRelics.Length != 0 || _relics.VisibleCount == 0)
            throw new Exception("遗物序列白名单控件包含捏奥专属遗物或配置往返错误。");
    }

    internal void VerifyNeowDetails(Hit hit)
    {
        _detail.Text = FormatDetail(hit);
        if (hit.neow == null || !_detail.Text.Contains("已应用捏奥") || hit.neow.cards.Any(c => !_detail.Text.Contains($"({c.card})"))
            || hit.neow.cards.Any(c => c.kind == "fixedCurse") && !_detail.Text.Contains("固定诅咒（不筛选）")
            || hit.neow.cards.Any(c => c.kind == "transform") && !_detail.Text.Contains("→")
            || hit.fights.Any(f => f.cards.Any(c => !_detail.Text.Contains($"({c})"))))
            throw new Exception("捏奥卡牌、变形、固定诅咒或后续奖励详情不完整。");
        _latest = null; _results.Items.Clear(); _pending.Clear(); _pending.Enqueue(hit); Drain(true);
        _status.Text = "捏奥卡牌与首战奖励详情样本"; _stats.Text = "全部随机候选参与白名单；固定诅咒只展示。";
    }

    internal async Task VerifyResultDelivery(string path)
    {
        ApplyConfig(new SearchConfig { count = 2000, minFightsChecked = 0, threads = 4, outFile = path });
        await StartSearch();
        if (_results.Items.Count != DisplayLimit || File.ReadLines(path).Count() != 2000 || !_start.Enabled || _stop.Enabled || _progress.Value != 1000)
            throw new Exception("结果限量显示、输出、进度或运行按钮状态错误。");
    }

    internal async Task VerifyFindUntilHit(string path)
    {
        ApplyConfig(new SearchConfig { findUntilHit = true, count = 1, minFightsChecked = 0, threads = 4, outFile = path });
        if (_count.Enabled || !ReadConfig().findUntilHit) throw new Exception("找到为止未禁用数量或未写入配置。");
        await StartSearch();
        if (_results.Items.Count != 1 || File.ReadLines(path).Count() != 1 || !_status.Text.Contains("已找到")
            || !_stats.Text.Contains("条件通过")
            || !_start.Enabled || _stop.Enabled || _progress.Value != 1000 || _progress.Style != ProgressBarStyle.Continuous)
            throw new Exception("找到为止未自动停止、结果重复或界面状态未恢复。");
        _untilFound.Checked = false;
        if (!_count.Enabled) throw new Exception("关闭找到为止未恢复数量设置。");
    }

    internal void PreviewRangeAndCards()
    {
        _configuration.SelectedIndex = 1; _filters.SelectedIndex = 1;
    }

    internal void PreviewEncounters() => _filters.SelectedIndex = 3;

    internal void PreviewMapSettings() => _configuration.SelectedIndex = 2;
}
