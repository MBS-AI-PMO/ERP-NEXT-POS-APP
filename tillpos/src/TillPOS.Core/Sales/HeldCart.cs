namespace TillPOS.Core.Sales;

public sealed record HeldLine(string ItemCode, string Uom, decimal Qty, string? Barcode, bool FromScaleLabel);

/// <summary>A parked bill (hold F5 / recall F7). Lines are re-priced when recalled.</summary>
public sealed record HeldCart(string Id, string Label, DateTimeOffset HeldAt, IReadOnlyList<HeldLine> Lines);
