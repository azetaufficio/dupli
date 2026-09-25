/** Bell badge text, or null to hide it. Caps at "9+" so the pill never grows past two glyphs. */
export function unreadBadgeLabel(count: number): string | null {
  if (count <= 0) return null;
  return count > 9 ? '9+' : String(count);
}
