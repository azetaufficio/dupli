import { unreadBadgeLabel } from './notification-badge';

describe('unreadBadgeLabel', () => {
  it('hides the badge when there is nothing unread', () => {
    expect(unreadBadgeLabel(0)).toBeNull();
    expect(unreadBadgeLabel(-1)).toBeNull();
  });

  it('shows the exact count up to 9', () => {
    expect(unreadBadgeLabel(1)).toBe('1');
    expect(unreadBadgeLabel(9)).toBe('9');
  });

  it('caps at "9+" beyond 9', () => {
    expect(unreadBadgeLabel(10)).toBe('9+');
    expect(unreadBadgeLabel(142)).toBe('9+');
  });
});
