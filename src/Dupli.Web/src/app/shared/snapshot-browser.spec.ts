import { toResticTreePath } from './snapshot-browser';

describe('toResticTreePath', () => {
  it('converts a Windows backslash path to restic tree-path form', () => {
    expect(toResticTreePath('D:\\SomeData')).toBe('/D/SomeData');
    expect(toResticTreePath('D:\\SomeData\\Nested\\Dir')).toBe('/D/SomeData/Nested/Dir');
  });

  it('converts a Windows forward-slash path to restic tree-path form', () => {
    expect(toResticTreePath('D:/SomeData')).toBe('/D/SomeData');
  });

  it('leaves a POSIX path unchanged', () => {
    expect(toResticTreePath('/data/foo')).toBe('/data/foo');
  });
});
