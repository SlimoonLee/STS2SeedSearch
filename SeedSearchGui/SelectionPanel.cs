namespace SeedSearchGui;

public sealed record SelectionOption(string Id, string Name, string Group = "", string Source = "", bool Primary = true);

/// <summary>过滤仅改变可见项，勾选状态由 ID 保存；切换列表不会遗留旧控件。</summary>
public sealed class SelectionPanel : UserControl
{
    readonly TextBox _filter = new() { Dock = DockStyle.Fill, PlaceholderText = "搜索中文名或 ID…" };
    readonly Label _count = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 0, 0) };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HideSelection = false, MultiSelect = true, ShowItemToolTips = true
    };
    List<SelectionOption> _options = [];
    readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    bool _rebuilding;
    public event EventHandler SelectionChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Filter { get => _filter.Text; set => _filter.Text = value; }
    public int OptionCount => _options.Count;
    public int VisibleCount => _list.Items.Count;
    public IEnumerable<string> OptionIds => _options.Select(o => o.Id);
    public string[] CheckedIds() => _options.Where(o => _selected.Contains(o.Id)).Select(o => o.Id).ToArray();

    public SelectionPanel(bool rarityButtons = false)
    {
        Dock = DockStyle.Fill;
        _list.Columns.Add("名称", 190); _list.Columns.Add("ID", 190); _list.Columns.Add("分组", 70);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_filter, 0, 0);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 3, 0, 3) };
        AddButton("全选可见", () => SetVisible(true));
        AddButton("清空可见", () => SetVisible(false));
        AddButton("取消全部", () => SelectIds([]));
        if (rarityButtons) AddButton("本职业普通 + 罕见", SelectOrdinary);
        bar.Controls.Add(_count);
        root.Controls.Add(bar, 0, 1); root.Controls.Add(_list, 0, 2); Controls.Add(root);
        _filter.TextChanged += (_, _) => Rebuild();
        _list.ItemChecked += (_, e) =>
        {
            if (_rebuilding) return;
            string id = (string)e.Item.Tag;
            if (e.Item.Checked) _selected.Add(id); else _selected.Remove(id);
            Changed();
        };
        _list.Resize += (_, _) =>
        {
            int width = Math.Max(280, _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
            _list.Columns[0].Width = width * 45 / 100;
            _list.Columns[1].Width = width * 40 / 100;
            _list.Columns[2].Width = width - _list.Columns[0].Width - _list.Columns[1].Width;
        };
        Changed();

        void AddButton(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(0, 0, 5, 0) };
            button.Click += (_, _) => action(); bar.Controls.Add(button);
        }
    }

    public void SetItems(IEnumerable<SelectionOption> options, IEnumerable<string> selected = null)
    {
        string[] prior = selected?.ToArray() ?? CheckedIds();
        _options = options.ToList(); _selected.Clear();
        var valid = _options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in prior) if (valid.Contains(id)) _selected.Add(id);
        Rebuild(); Changed();
    }
    public void SelectIds(IEnumerable<string> ids)
    {
        _selected.Clear();
        var valid = _options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in ids) if (valid.Contains(id)) _selected.Add(id);
        Rebuild(); Changed();
    }
    public void SetVisible(bool check)
    {
        foreach (ListViewItem item in _list.Items)
            if (check) _selected.Add((string)item.Tag); else _selected.Remove((string)item.Tag);
        Rebuild(); Changed();
    }
    internal void SelectOrdinary() => SelectIds(_options.Where(o => o.Primary && o.Group is "普通" or "罕见").Select(o => o.Id));
    void Changed()
    {
        _count.Text = $"已选 {_selected.Count}/{_options.Count} · 空=不限";
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    void Rebuild()
    {
        _rebuilding = true; _list.BeginUpdate();
        try
        {
            _list.Items.Clear(); _list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            var words = _filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var option in _options)
            {
                string text = $"{option.Name} {option.Id} {SeedSearch.Game.Snake(option.Id)} {option.Group} {option.Source}";
                if (!words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
                var item = new ListViewItem(new[] { option.Name, option.Id, option.Group })
                {
                    Tag = option.Id, Checked = _selected.Contains(option.Id), ToolTipText = text,
                    ForeColor = option.Group switch { "罕见" => Color.RoyalBlue, "稀有" => Color.DarkGoldenrod, "诅咒" => Color.Purple, "成对遗物" => Color.Teal, _ => Color.Black }
                };
                if (option.Group.Length > 0)
                {
                    string heading = option.Source.Length == 0 ? option.Group : $"{option.Source} · {option.Group}";
                    if (!groups.TryGetValue(heading, out var group))
                    { group = new ListViewGroup(heading); groups[heading] = group; _list.Groups.Add(group); }
                    item.Group = group;
                }
                _list.Items.Add(item);
            }
        }
        finally { _list.EndUpdate(); _rebuilding = false; }
    }
}
