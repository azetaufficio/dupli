import { readCookie } from './auth.service';

describe('readCookie', () => {
  it('reads and decodes a cookie by name', () => {
    document.cookie = 'XSRF-TOKEN=a%2Bb%3D';
    document.cookie = 'other=1';
    expect(readCookie('XSRF-TOKEN')).toBe('a+b=');
    expect(readCookie('missing')).toBeNull();
  });
});
