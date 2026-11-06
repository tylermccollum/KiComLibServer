import { definitionBlobs, definitionChanges, reviewRequestUuid } from './review.service';

describe('Review definition comparison', () => {
  it('compares values independent of object insertion order', () => {
    expect(definitionChanges({ a: 1, b: 2 }, { b: 2, a: 1 })).toEqual([]);
  });
  it('reports shared dependency changes and ordered source changes', () => {
    expect(definitionChanges({ symbol: { parent: { blobHash: 'old' } }, sources: ['a', 'b'] },
      { symbol: { parent: { blobHash: 'new' } }, sources: ['b', 'a'] }).map(c => c.path))
      .toEqual(['/sources/0', '/sources/1', '/symbol/parent/blobHash']);
  });
  it('finds inherited and nested model evidence', () => {
    expect(definitionBlobs({ symbol: { blobHash: 'a', parent: { blobHash: 'b' } }, models: [{ blobHash: 'c' }] }))
      .toEqual([{ path: '/symbol', hash: 'a' }, { path: '/symbol/parent', hash: 'b' }, { path: '/models/0', hash: 'c' }]);
  });
  it('uses UUID v4 action keys even on internal HTTP origins', () => {
    expect(reviewRequestUuid()).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  });
});
