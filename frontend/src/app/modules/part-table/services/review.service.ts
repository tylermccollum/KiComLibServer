import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { appEnvironment } from '../../../app-environment';

export interface Finding { ruleId: string; severity: string; entityPath: string; message: string; }
export interface DefinitionSummary { definitionDigest: string; state: string; stateVersion: number; createdAt?: string; }
export interface ReviewDefinition extends DefinitionSummary {
  body: Record<string, unknown>;
  provenance: unknown[];
  findings?: Finding[];
  approvalValid: boolean;
  isCurrent?: boolean;
  validation?: { report: { findings: Finding[] } };
}
export interface ReviewEvent {
  id: number; action: string; comment?: string; createdAt: string;
  actor: { username: string }; result: { fromState: string; state: string; findings: Finding[] };
}
export interface AffectedDefinition extends DefinitionSummary { partUuid: string; name: string; selected?: boolean; }

@Injectable({ providedIn: 'root' })
export class ReviewService {
  private readonly url = appEnvironment.apiUrl;
  constructor(private http: HttpClient) {}
  current(uuid: string) { return this.http.get<ReviewDefinition>(`${this.url}/parts/${uuid}/review`); }
  definitions(uuid: string) { return this.http.get<DefinitionSummary[]>(`${this.url}/parts/${uuid}/review-definitions`); }
  definition(digest: string) { return this.http.get<ReviewDefinition>(`${this.url}/review-definitions/${digest}`); }
  history(digest: string, before = 0) { return this.http.get<{ items: ReviewEvent[]; nextBefore: number }>(`${this.url}/review-definitions/${digest}/history?before=${before}`); }
  affected(hash: string) { return this.http.get<AffectedDefinition[]>(`${this.url}/reviews/affected/${hash}`); }
  blob(digest: string, hash: string) { return this.http.get(`${this.url}/review-definitions/${digest}/blobs/${hash}`, { responseType: 'blob' }); }
  act(uuid: string, definition: DefinitionSummary, action: string, comment: string, requestUuid: string) {
    return this.http.post(`${this.url}/parts/${uuid}/review-actions`, {
      action, definitionDigest: definition.definitionDigest, expectedStateVersion: definition.stateVersion, comment, requestUuid
    });
  }
}

/** UUID generation also works on an internal HTTP origin. */
export function reviewRequestUuid(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export interface DefinitionChange { path: string; before: string; after: string; }
export function definitionChanges(before: unknown, after: unknown, path = ''): DefinitionChange[] {
  if (JSON.stringify(before) === JSON.stringify(after)) return [];
  if (before && after && typeof before === 'object' && typeof after === 'object'
      && Array.isArray(before) === Array.isArray(after)) {
    const a = before as Record<string, unknown>, b = after as Record<string, unknown>;
    return [...new Set([...Object.keys(a), ...Object.keys(b)])].sort()
      .flatMap(key => definitionChanges(a[key], b[key], `${path}/${key}`));
  }
  return [{ path: path || '/', before: JSON.stringify(before) ?? '(absent)', after: JSON.stringify(after) ?? '(absent)' }];
}

export function definitionBlobs(body: unknown): { path: string; hash: string }[] {
  const result: { path: string; hash: string }[] = [];
  function visit(node: unknown, path: string) {
    if (!node || typeof node !== 'object') return;
    for (const [key, value] of Object.entries(node)) {
      if (key === 'blobHash' && typeof value === 'string') result.push({ path, hash: value });
      else visit(value, `${path}/${key}`);
    }
  }
  visit(body, '');
  return result;
}
