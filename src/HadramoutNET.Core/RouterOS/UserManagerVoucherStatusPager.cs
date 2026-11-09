namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Local-only search and 300-row pagination over one authoritative user
/// snapshot. No network request per page, search keystroke or dialog.
/// </summary>
public sealed class UserManagerVoucherStatusPager
{
    public const int PageSize = 300;
    private readonly UserManagerLifecycleRow[] _source;
    private IReadOnlyList<UserManagerLifecycleRow> _matching;
    private string _search = "";

    public UserManagerVoucherStatusPager(
        IReadOnlyList<UserManagerLifecycleRow> rows, string status)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (status is not ("active" or "expired"))
            throw new ArgumentException("Card status is not supported.", nameof(status));
        _source = rows.Where(row => row.State == status).ToArray();
        _matching = _source;
    }

    public int Count => _matching.Count;

    public int Pages => (Count + PageSize - 1) / PageSize;

    public string Search
    {
        get => _search;
        set
        {
            string query = (value ?? "").Trim();
            if (query == _search) return;
            _search = query;
            _matching = _search.Length == 0 ? _source :
                _source.Where(item =>
                    item.Username.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                    item.Profile.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                    item.Reason.Contains(_search, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
        }
    }

    public IReadOnlyList<UserManagerLifecycleRow> Page(int page)
    {
        if (page < 0 || page >= Pages) return [];
        return _matching.Skip(page * PageSize).Take(PageSize).ToArray();
    }
}
