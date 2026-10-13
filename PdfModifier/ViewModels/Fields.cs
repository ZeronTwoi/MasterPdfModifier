using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfModifier.Core;
using PdfModifier.Localization;

namespace PdfModifier.ViewModels;

/// <summary>One input (or output) row in a tool's option panel. The view picks a template by the field's type.</summary>
public abstract partial class Field(string labelKey) : LocalizedObject
{
    public string Label => labelKey.Length == 0 ? "" : Loc.Get(labelKey);

    [ObservableProperty] private bool _isVisible = true;

    /// <summary>
    /// Raised when the user changed what the field holds, as opposed to the field being shown, hidden or relabelled.
    /// Tools use it to refresh the preview.
    /// </summary>
    public event Action? Edited;

    /// <summary>False for fields that only show something (hints, results, buttons): they have nothing to edit.</summary>
    protected virtual bool IsInput => true;

    /// <summary>
    /// False for the properties of a field that say something about what it holds rather than being it (a line
    /// that describes the choice, a mark that it is wrong): their changing is no edit.
    /// </summary>
    protected virtual bool IsSetting(string property) => true;

    protected void OnEdited() => Edited?.Invoke();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // An empty property name is the language-change broadcast.
        if (IsInput && !string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(IsVisible) && IsSetting(e.PropertyName)) OnEdited();
    }
}

public partial class TextField(string labelKey, string value = "", bool multiline = false) : Field(labelKey)
{
    [ObservableProperty] private string _value = value;
    public bool Multiline => multiline;

    /// <summary>Masks the input, for passwords.</summary>
    public bool IsPassword { get; init; }

    /// <summary>Shows a password as it is typed, for whoever wants to check it.</summary>
    [ObservableProperty] private bool _reveal;

    public char PasswordChar => IsPassword && !Reveal ? '•' : default;

    partial void OnRevealChanged(bool value) => OnPropertyChanged(nameof(PasswordChar));

    protected override bool IsSetting(string property) => property is not (nameof(Reveal) or nameof(PasswordChar));

    /// <summary>Asks the view to put the keyboard focus into this field.</summary>
    public event Action? FocusRequested;

    public void Focus() => FocusRequested?.Invoke();
}

public partial class NumberField(string labelKey, decimal value, decimal minimum, decimal maximum, decimal increment = 1, string format = "0")
    : Field(labelKey)
{
    [ObservableProperty] private decimal? _value = value;
    public decimal Minimum => minimum;
    public decimal Maximum => maximum;
    public decimal Increment => increment;
    public string Format => format;

    /// <summary>The value as a number, falling back to the minimum when the box was cleared.</summary>
    public double Number => (double)(Value ?? minimum);

    public void Set(double number) => Value = Math.Clamp((decimal)Math.Round(number, 2), minimum, maximum);
}

/// <summary>One of the values offered under a slider for a single click: the ones that are wanted most of the time.</summary>
public sealed record SliderStop(decimal Value, string Label);

/// <summary>A number chosen by dragging, with the exact value beside it: for strengths, opacities and angles.</summary>
public partial class SliderField(string labelKey, decimal value, decimal minimum, decimal maximum, decimal increment = 1, string format = "0")
    : NumberField(labelKey, value, minimum, maximum, increment, format)
{
    /// <summary>The same value as a double, which is what a slider binds to.</summary>
    public double Position
    {
        get => Number;
        set => Value = Math.Clamp(Math.Round((decimal)value / Increment) * Increment, Minimum, Maximum);
    }

    /// <summary>Values that can be had with one click, shown as small buttons under the slider.</summary>
    public IReadOnlyList<SliderStop> Stops { get; private set; } = [];

    public bool HasStops => Stops.Count > 0;

    /// <summary>Offers these values under the slider, each written with <paramref name="unit"/> after it.</summary>
    public SliderField WithStops(string unit, params decimal[] values)
    {
        Stops = values.Select(stop => new SliderStop(stop, stop.ToString(Format, CultureInfo.CurrentCulture) + unit)).ToList();
        return this;
    }

    [RelayCommand]
    private void Use(SliderStop stop) => Value = Math.Clamp(stop.Value, Minimum, Maximum);

    protected override bool IsSetting(string property) => property != nameof(Position);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Value)) OnPropertyChanged(nameof(Position));
    }
}

public partial class BoolField(string labelKey, bool value = false) : Field(labelKey)
{
    [ObservableProperty] private bool _value = value;
}

/// <summary>An on/off switch shown as a small button with a symbol on it (bold, italic), for use in a row.</summary>
public sealed class ToggleField(string labelKey, string symbol, bool value = false) : BoolField(labelKey, value)
{
    public string Symbol => symbol;
}

public sealed class Choice(string key, string labelKey, bool literal = false) : LocalizedObject
{
    public string Key => key;
    public string Label => literal ? labelKey : Loc.Get(labelKey);

    /// <summary>A small drawing that stands for the choice where choices are shown as buttons; its label is then the tip.</summary>
    public Geometry? Icon { get; init; }

    public bool HasIcon => Icon != null;

    public override string ToString() => Label;
}

public partial class ChoiceField : Field
{
    [ObservableProperty] private Choice _selected;

    public ChoiceField(string labelKey, params Choice[] options) : base(labelKey)
    {
        Options = options;
        _selected = options[0];
    }

    public IReadOnlyList<Choice> Options { get; private set; }
    public string Key => Selected.Key;

    public void Select(string key) => Selected = Options.FirstOrDefault(o => o.Key == key) ?? Options[0];

    /// <summary>Replaces what can be chosen, keeping the current choice when it is still on offer.</summary>
    public void SetOptions(IReadOnlyList<Choice> options)
    {
        var key = Key;
        Options = options;
        OnPropertyChanged(nameof(Options));
        Select(key);
    }
}

/// <summary>
/// A choice between a few things shown as buttons, one of which is pressed: all of them are on view and one click
/// chooses. Side by side in one row, or in rows of <see cref="Columns"/> where the labels are long.
/// </summary>
public sealed class ModeField(string labelKey, params Choice[] options) : ChoiceField(labelKey, options)
{
    /// <summary>How many buttons stand in a row; 0 puts them all into one.</summary>
    public int Columns { get; init; }

    public int Rows => Columns == 0 ? 1 : 0;

    /// <summary>
    /// What the pressed button stands for, written out under the buttons when they show drawings instead of words.
    /// </summary>
    public string Caption => Options.Any(option => option.HasIcon) ? Selected.Label : "";

    public bool HasCaption => Caption.Length > 0;

    protected override bool IsSetting(string property) => property is not (nameof(Caption) or nameof(HasCaption));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Selected) or nameof(Options)) OnPropertyChanged(nameof(Caption));
    }
}

/// <summary>
/// Which pages an operation applies to: all, those selected in the page list, or a range that is typed. Under the
/// choice it says which pages that comes to, so nobody has to look at the page list to know.
/// </summary>
public partial class PagesField : Field
{
    [ObservableProperty] private Choice _scope;
    [ObservableProperty] private string _range = "";

    /// <summary>The pages the choice comes to right now, in words: "3 pages: 2, 5-6".</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>True while the choice names no page at all or cannot be read.</summary>
    [ObservableProperty] private bool _isInvalid;

    private IReadOnlyList<int> _selected = [];
    private int _current, _count;

    public PagesField(string labelKey = "Pages") : base(labelKey)
    {
        Scopes = [new Choice("all", "PagesAll"), new Choice("selected", "PagesSelected"), new Choice("range", "PagesRange")];
        _scope = Scopes[0];
    }

    public IReadOnlyList<Choice> Scopes { get; }
    public bool IsRange => Scope.Key == "range";

    partial void OnScopeChanged(Choice value)
    {
        OnPropertyChanged(nameof(IsRange));
        Describe();
    }

    partial void OnRangeChanged(string value) => Describe();

    protected override bool IsSetting(string property) => property is not (nameof(Summary) or nameof(IsInvalid) or nameof(IsRange));

    /// <summary>The chosen page indexes, or null for "all pages".</summary>
    public List<int>? Resolve(IReadOnlyList<int> selectedPages, int currentPage, int pageCount) => Scope.Key switch
    {
        "selected" => selectedPages.Count > 0 ? selectedPages.Order().ToList() : [currentPage],
        "range" => PageRange.Parse(Range, pageCount).Distinct().Order().ToList(),
        _ => null
    };

    /// <summary>Tells the field what is selected in the page list, so it can say which pages its choice comes to.</summary>
    public void Show(IReadOnlyList<int> selectedPages, int currentPage, int pageCount)
    {
        (_selected, _current, _count) = (selectedPages, currentPage, pageCount);
        Describe();
    }

    private void Describe()
    {
        if (_count <= 0)
        {
            (Summary, IsInvalid) = ("", false);
            return;
        }

        try
        {
            var pages = Resolve(_selected, _current, _count);
            IsInvalid = pages is { Count: 0 };
            Summary = pages switch
            {
                // A document of a single page has no "all" worth counting.
                null => _count == 1 ? Loc.Format("PagesChosenOne", 1) : Loc.Format("PagesAllCount", _count),
                { Count: 0 } => Loc.Get("PagesNone"),
                { Count: 1 } => Loc.Format("PagesChosenOne", pages[0] + 1),
                _ => Loc.Format("PagesChosen", pages.Count, Shortened(PageRange.Format(pages)))
            };
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
        {
            (Summary, IsInvalid) = (Loc.Get("ErrPageRange"), true);
        }

        // A long list of single pages is cut short: the count before it says how many there are.
        static string Shortened(string list) =>
            list.Length <= 64 || list.LastIndexOf(',', 60) is not (> 0 and var end) ? list : list[..end] + ", …";
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // An empty property name is the language-change broadcast.
        if (string.IsNullOrEmpty(e.PropertyName)) Describe();
    }
}

/// <summary>A file used before, offered again under the Browse button. Images show a small picture of themselves.</summary>
public sealed partial class RecentFile(string path) : ObservableObject
{
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isCurrent;

    /// <summary>Pinned files stay on offer however many others are used after them.</summary>
    [ObservableProperty] private bool _isPinned;

    /// <summary>Set by the field the file is listed under: pins it or lets go of it, and takes it off the list.</summary>
    public ICommand? TogglePinCommand { get; set; }

    public ICommand? ForgetCommand { get; set; }

    public string Path => path;
    public string Name => System.IO.Path.GetFileName(path);
}

public partial class FileField(string labelKey, bool images = false) : Field(labelKey)
{
    [ObservableProperty] private string _path = "";
    public bool Images => images;
    public string FileName => System.IO.Path.GetFileName(Path);
    public Func<FileField, Task>? Browse { get; set; }

    /// <summary>Files chosen earlier, newest first; picking one is the same as browsing to it again.</summary>
    public ObservableCollection<RecentFile> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    public FileField WithRecent(IEnumerable<RecentFile> files)
    {
        Recent.Clear();
        foreach (var file in files)
        {
            // The commands sit on the entry itself, where its buttons and its menu both find them.
            file.TogglePinCommand = new RelayCommand(() => Pin?.Invoke(file, !file.IsPinned));
            file.ForgetCommand = new RelayCommand(() => Forget?.Invoke(file));
            Recent.Add(file);
        }

        MarkCurrent();
        OnPropertyChanged(nameof(HasRecent));
        return this;
    }

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(FileName));
        MarkCurrent();
    }

    private void MarkCurrent()
    {
        foreach (var file in Recent) file.IsCurrent = file.Path.Equals(Path, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private Task BrowseFile() => Browse?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>Set by the main view model: pins a file of the list or lets go of it, and takes one off the list.</summary>
    public Action<RecentFile, bool>? Pin { get; set; }

    public Action<RecentFile>? Forget { get; set; }

    [RelayCommand]
    private void UseRecent(RecentFile file)
    {
        // Set even when it is the current file again: choosing it is a request to use it once more.
        if (Path == file.Path) OnPropertyChanged(nameof(Path));
        else Path = file.Path;
    }

}

/// <summary>A folder, typed, pasted or browsed to.</summary>
public partial class FolderField(string labelKey) : Field(labelKey)
{
    [ObservableProperty] private string _path = "";

    public Func<FolderField, Task>? Browse { get; set; }

    [RelayCommand]
    private Task BrowseFolder() => Browse?.Invoke(this) ?? Task.CompletedTask;
}

/// <summary>An ordered list of files, e.g. the documents to merge.</summary>
public partial class FileListField : Field
{
    [ObservableProperty] private string? _selected;

    public FileListField(string labelKey, bool images = false) : base(labelKey)
    {
        Images = images;
        Files.CollectionChanged += (_, _) => OnEdited();
    }

    public ObservableCollection<string> Files { get; } = [];
    public bool Images { get; }
    public Func<FileListField, Task>? Browse { get; set; }

    [RelayCommand]
    private Task Add() => Browse?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void Remove()
    {
        if (Selected != null) Files.Remove(Selected);
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (Selected == null) return;
        int from = Files.IndexOf(Selected), to = from + delta;
        if (to < 0 || to >= Files.Count) return;
        var item = Selected;
        Files.Move(from, to);
        Selected = item;
    }
}

/// <summary>Read-only text: hints and results.</summary>
public partial class InfoField(string text = "") : Field("")
{
    [ObservableProperty] private string _text = text;

    /// <summary>Shown with a warning sign and an outline: for the rare note that wants to be noticed.</summary>
    [ObservableProperty] private bool _warns;

    private string? _key;

    protected override bool IsInput => false;

    /// <summary>False while there is nothing to say: the box that holds the text is then left out altogether.</summary>
    public bool HasText => Text.Length > 0;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasText));

    /// <summary>Shows a localised text that follows later language changes.</summary>
    public void SetKey(string key)
    {
        _key = key;
        Text = Loc.Get(key);
    }

    public void SetText(string text)
    {
        _key = null;
        Text = text;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // An empty property name is the language-change broadcast.
        if (string.IsNullOrEmpty(e.PropertyName) && _key != null) Text = Loc.Get(_key);
    }
}

public sealed class ActionField(string labelKey, Func<Task> action) : Field(labelKey)
{
    public ICommand Command { get; } = new AsyncRelayCommand(action);

    /// <summary>A small drawing shown on the button with its label.</summary>
    public Geometry? Icon { get; init; }

    public bool HasIcon => Icon != null;

    /// <summary>
    /// Puts the drawing above the label instead of beside it: for a few large buttons in a row that are what the
    /// tool is about (turn left, turn right).
    /// </summary>
    public bool Large { get; init; }

    public bool Beside => !Large;

    protected override bool IsInput => false;
}

/// <summary>One of the spots of a <see cref="PlaceField"/>.</summary>
public sealed partial class PlaceSpot(string key, string labelKey, bool offered) : LocalizedObject
{
    [ObservableProperty] private bool _isChosen;

    public string Key => key;
    public string Label => Loc.Get(labelKey);

    /// <summary>False for a spot that is only there to keep the others in their places: it cannot be chosen.</summary>
    public bool IsOffered => offered;
}

/// <summary>
/// Where on the page something goes, chosen by clicking the spot on a small picture of a page: its corners, the
/// middles of its sides and its centre, or those of them that the tool offers.
/// </summary>
public sealed partial class PlaceField : Field
{
    /// <summary>The nine spots, row by row from the top left. Their names are those of <c>StampPosition</c> where it has them.</summary>
    public static readonly IReadOnlyList<(string Key, string LabelKey)> All =
    [
        ("TopLeft", "PosTopLeft"), ("TopCenter", "PosTopCenter"), ("TopRight", "PosTopRight"),
        ("MiddleLeft", "PosMiddleLeft"), ("Center", "PosCenter"), ("MiddleRight", "PosMiddleRight"),
        ("BottomLeft", "PosBottomLeft"), ("BottomCenter", "PosBottomCenter"), ("BottomRight", "PosBottomRight")
    ];

    /// <summary>The chosen spot; null once the place was set some other way (a click on the page itself).</summary>
    [ObservableProperty] private string? _key;

    /// <param name="offered">The spots that can be chosen; none given offers all nine.</param>
    public PlaceField(string labelKey, string chosen, params string[] offered) : base(labelKey)
    {
        Spots = All.Select(spot => new PlaceSpot(spot.Key, spot.LabelKey, offered.Length == 0 || offered.Contains(spot.Key))).ToList();
        _key = chosen;
        Mark();
    }

    public IReadOnlyList<PlaceSpot> Spots { get; }

    /// <summary>Where the chosen spot lies across and down the page (0, 0.5 or 1 each); the middle when none is chosen.</summary>
    public (double X, double Y) Share
    {
        get
        {
            var index = Math.Max(0, All.Select(spot => spot.Key).ToList().IndexOf(Key ?? "Center"));
            return (index % 3 / 2.0, index / 3 / 2.0);
        }
    }

    partial void OnKeyChanged(string? value) => Mark();

    private void Mark()
    {
        foreach (var spot in Spots) spot.IsChosen = spot.Key == Key;
    }

    [RelayCommand]
    private void Choose(PlaceSpot spot)
    {
        if (spot.IsOffered) Key = spot.Key;
    }
}

public partial class CheckItem(string key, string label, bool isChecked = false) : ObservableObject
{
    [ObservableProperty] private bool _isChecked = isChecked;
    [ObservableProperty] private string _label = label;
    public string Key => key;
}

/// <summary>A list of tick boxes: several of them can be on at once.</summary>
public sealed class CheckListField : Field
{
    public CheckListField(string labelKey) : base(labelKey)
    {
        Items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (CheckItem item in e.NewItems)
                    item.PropertyChanged += ItemChanged;
            if (e.OldItems != null)
                foreach (CheckItem item in e.OldItems)
                    item.PropertyChanged -= ItemChanged;
            OnEdited();
        };
    }

    public ObservableCollection<CheckItem> Items { get; } = [];
    public IEnumerable<string> CheckedKeys => Items.Where(i => i.IsChecked).Select(i => i.Key);

    public bool IsChecked(string key) => Items.Any(i => i.Key == key && i.IsChecked);

    private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CheckItem.IsChecked)) OnEdited();
    }
}

/// <summary>One of the small pictures of a <see cref="PieceListField"/>.</summary>
public sealed partial class PieceItem(object source) : ObservableObject
{
    [ObservableProperty] private IImage? _picture;
    [ObservableProperty] private string _label = "";

    /// <summary>Width over height of what the picture shows, so the item has its shape before the picture is there.</summary>
    [ObservableProperty] private double _aspect = 0.7;

    /// <summary>Whatever the owner of the list ties to this item.</summary>
    public object Source => source;

    public double DisplayHeight => 84;
    public double DisplayWidth => Math.Clamp(DisplayHeight * Aspect, 32, 150);

    partial void OnAspectChanged(double value) => OnPropertyChanged(nameof(DisplayWidth));
}

/// <summary>Small numbered pictures in a row: the pages a tool is about to make, in their order. Each can be clicked.</summary>
public sealed partial class PieceListField(string labelKey) : Field(labelKey)
{
    public ObservableCollection<PieceItem> Items { get; } = [];

    /// <summary>Called when one of the pictures is clicked.</summary>
    public Action<PieceItem>? Chosen { get; set; }

    protected override bool IsInput => false;

    [RelayCommand]
    private void Choose(PieceItem item) => Chosen?.Invoke(item);
}

/// <summary>Several fields next to each other on one line.</summary>
public sealed class RowField : Field
{
    public RowField(params Field[] fields) : base("")
    {
        Fields = fields;
        foreach (var field in fields)
        {
            field.Edited += OnEdited;
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IsVisible)) IsVisible = Fields.Any(f => f.IsVisible);
            };
        }

        IsVisible = Fields.Any(f => f.IsVisible);
    }

    protected override bool IsInput => false;

    public IReadOnlyList<Field> Fields { get; }
}

/// <summary>One of the ready-made colours offered under a colour field.</summary>
public sealed class Swatch(string hex)
{
    public string Hex => hex;
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(hex));
}

/// <summary>
/// A colour, typed as #RRGGBB, picked from a few ready-made ones, or taken from the page with a click.
/// With <see cref="AllowAutomatic"/> the colour can also be left to the tool.
/// </summary>
public partial class ColorField : Field
{
    private static readonly string[] Palette =
        ["#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF", "#C00000", "#E36C09", "#F2C200", "#2E8B2E", "#1F5FBF", "#7030A0"];

    [ObservableProperty] private string _value;
    [ObservableProperty] private bool _automatic;
    [ObservableProperty] private bool _isPicking;

    public ColorField(string labelKey, string value = "#000000", bool allowAutomatic = false) : base(labelKey)
    {
        _value = value;
        AllowAutomatic = allowAutomatic;
        _automatic = allowAutomatic;
        Swatches = Palette.Select(hex => new Swatch(hex)).ToList();
    }

    public bool AllowAutomatic { get; }
    public IReadOnlyList<Swatch> Swatches { get; }
    public string AutomaticLabel => Loc.Get("ColorAutomatic");

    /// <summary>Set by the main view model: starts and ends taking a colour from the page.</summary>
    public Action<ColorField>? PickFromPage { get; set; }

    /// <summary>The colour as opaque ARGB, or null while what is typed is not a colour.</summary>
    public uint? Argb => TryParse(Value, out var argb) ? argb : null;

    public bool IsValid => Argb != null;

    public IBrush Preview => Argb is { } argb
        ? new SolidColorBrush(Color.FromUInt32(argb))
        : Brushes.Transparent;

    public void Set(uint argb) => Value = $"#{argb & 0xFFFFFF:X6}";

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(IsValid));
        // Choosing a colour by hand means not leaving it to the tool any more.
        if (Automatic && IsValid) Automatic = false;
    }

    [RelayCommand]
    private void Use(Swatch swatch) => Value = swatch.Hex;

    [RelayCommand]
    private void Pick() => PickFromPage?.Invoke(this);

    /// <summary>Reads "#RRGGBB" or "#RGB" (the # is optional) as an opaque ARGB colour.</summary>
    public static bool TryParse(string text, out uint argb)
    {
        argb = 0;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        argb = 0xFF000000 | rgb;
        return true;
    }
}
