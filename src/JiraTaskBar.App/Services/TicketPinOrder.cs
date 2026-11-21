namespace JiraTaskBar.App.Services;

internal static class TicketPinOrder
{
    public static bool Place(List<string> pins, string key, string? anchor, bool after, bool moveExisting)
    {
        var current = IndexOf(pins, key);
        if (current >= 0 && (!moveExisting || string.Equals(key, anchor, StringComparison.OrdinalIgnoreCase))) return false;
        if (current >= 0) pins.RemoveAt(current);
        var anchorIndex = IndexOf(pins, anchor);
        var destination = anchorIndex < 0 ? pins.Count : anchorIndex + (after ? 1 : 0);
        pins.Insert(destination, key);
        return destination != current;
    }

    public static bool Move(List<string> pins, string key, int direction)
    {
        var current = IndexOf(pins, key);
        var destination = current + direction;
        if (current < 0 || destination < 0 || destination >= pins.Count) return false;
        (pins[current], pins[destination]) = (pins[destination], pins[current]);
        return true;
    }

    private static int IndexOf(List<string> pins, string? key) =>
        pins.FindIndex(pin => string.Equals(pin, key, StringComparison.OrdinalIgnoreCase));
}
