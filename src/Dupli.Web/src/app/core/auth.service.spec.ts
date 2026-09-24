import { hasRole, isStandalonePath, readCookie } from './auth.service';

describe('readCookie', () => {
  it('reads and decodes a cookie by name', () => {
    document.cookie = 'XSRF-TOKEN=a%2Bb%3D';
    document.cookie = 'other=1';
    expect(readCookie('XSRF-TOKEN')).toBe('a+b=');
    expect(readCookie('missing')).toBeNull();
  });
});

describe('hasRole', () => {
  it('orders Viewer < Operator < Owner', () => {
    expect(hasRole('Owner', 'Operator')).toBe(true);
    expect(hasRole('Operator', 'Operator')).toBe(true);
    expect(hasRole('Viewer', 'Operator')).toBe(false);
    expect(hasRole(null, 'Viewer')).toBe(false);
  });
});

describe('isStandalonePath', () => {
  it('matches the break-glass and access-denied pages only', () => {
    expect(isStandalonePath('/admin')).toBe(true);
    expect(isStandalonePath('/access-denied')).toBe(true);
    expect(isStandalonePath('/administrator')).toBe(false);
    expect(isStandalonePath('/agents')).toBe(false);
  });
});
