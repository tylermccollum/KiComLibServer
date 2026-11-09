import { Component, Input, Output, EventEmitter, OnChanges, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom, forkJoin } from 'rxjs';
import { AuthService } from '../../../../services/auth.service';
import {
  AffectedDefinition, DefinitionSummary, ReviewDefinition, ReviewEvent, ReviewService,
  definitionBlobs, definitionChanges, reviewRequestUuid
} from '../../services/review.service';

@Component({
  selector: 'part-review', standalone: true, imports: [CommonModule, FormsModule],
  templateUrl: './part-review.component.html', changeDetection: ChangeDetectionStrategy.Eager,
  styles: [`:host { display:block; padding:1rem 0; } code { overflow-wrap:anywhere; }
    table { width:100%; font-size:.8rem; } td, th { text-align:left; padding:.5rem; border-bottom:1px solid #374151; vertical-align:top; }
    .actions { display:flex; flex-wrap:wrap; gap:.5rem; margin:1rem 0; } details { margin:1rem 0; } summary { cursor:pointer; }
    .review-status-line { display:flex; align-items:center; flex-wrap:wrap; gap:.625rem; }
    .review-status-badge { display:inline-flex; align-items:center; border-radius:.375rem; padding:.375rem .75rem;
      background:#dc2626; color:#fff; font-size:.875rem; font-weight:600; line-height:1.25rem; }
    .review-status-badge.is-approved { background:#15803d; }
    .review-status-badge.is-pending { background:#facc15; color:#422006; }
    .error { color:#fca5a5; } .muted { color:#9ca3af; }`]
})
export class PartReviewComponent implements OnChanges {
  @Input({ required: true }) partUuid = '';
  @Output() changed = new EventEmitter<void>();
  current?: ReviewDefinition;
  shown?: ReviewDefinition;
  definitions: DefinitionSummary[] = [];
  events: ReviewEvent[] = [];
  nextBefore = 0;
  selectedDigest = '';
  compareDigest = '';
  comparison?: ReviewDefinition;
  comment = '';
  busy = false;
  error = '';
  affected: AffectedDefinition[] = [];
  readonly changes = definitionChanges;
  readonly blobs = definitionBlobs;
  private generation = 0;
  private retry?: { key: string; uuid: string };

  constructor(public auth: AuthService, private reviews: ReviewService) {}
  ngOnChanges() { if (this.partUuid) void this.reload(); }
  get reviewer() { return this.auth.profile().isAdmin && this.auth.canWrite(); }
  get isCurrent() { return this.shown?.definitionDigest === this.current?.definitionDigest; }
  get findings() { return this.shown?.findings ?? this.shown?.validation?.report.findings ?? []; }

  async reload(keepDigest = '') {
    const generation = ++this.generation;
    this.busy = true;
    try {
      const data = await firstValueFrom(forkJoin({ current: this.reviews.current(this.partUuid), definitions: this.reviews.definitions(this.partUuid) }));
      if (generation !== this.generation) return;
      this.current = data.current; this.definitions = data.definitions;
      await this.select(keepDigest || data.current.definitionDigest);
    } catch (e) { this.fail(e); }
    finally { if (generation === this.generation) this.busy = false; }
  }
  async select(digest: string) {
    this.selectedDigest = digest; this.affected = []; this.events = []; this.nextBefore = 0;
    try {
      const shown = digest === this.current?.definitionDigest ? this.current : await firstValueFrom(this.reviews.definition(digest));
      if (this.selectedDigest !== digest) return;
      this.shown = shown;
      if (digest && this.definitions.some(d => d.definitionDigest === digest)) await this.moreHistory(true);
    } catch (e) { this.fail(e); }
  }
  async compare(digest: string) {
    this.compareDigest = digest;
    this.comparison = undefined;
    if (!digest) return;
    try {
      const result = await firstValueFrom(this.reviews.definition(digest));
      if (this.compareDigest === digest) this.comparison = result;
    } catch (e) { this.fail(e); }
  }
  async moreHistory(reset = false) {
    const digest = this.selectedDigest;
    try {
      const result = await firstValueFrom(this.reviews.history(digest, reset ? 0 : this.nextBefore));
      if (this.selectedDigest !== digest) return;
      this.events = reset ? result.items : [...this.events, ...result.items];
      this.nextBefore = result.items.length === 50 ? result.nextBefore : 0;
    } catch (e) { this.fail(e); }
  }
  async act(action: string) {
    if (!this.shown || this.busy) return;
    this.busy = true; this.error = '';
    const digest = this.shown.definitionDigest;
    const key = JSON.stringify([digest, this.shown.stateVersion, action, this.comment]);
    if (this.retry?.key !== key) this.retry = { key, uuid: reviewRequestUuid() };
    try {
      await firstValueFrom(this.reviews.act(this.partUuid, this.shown, action, this.comment, this.retry.uuid));
      this.retry = undefined; this.comment = ''; this.changed.emit();
      await this.reload(digest);
    } catch (e) {
      this.fail(e);
      // A received error has a definite outcome; refresh stale state or failed evidence.
      // A network failure retains the request UUID for a safe retry.
      if ((e as { status?: number }).status) { this.retry = undefined; await this.reload(digest); }
    } finally { this.busy = false; }
  }
  async download(hash: string) {
    if (!this.shown) return;
    try {
      const blob = await firstValueFrom(this.reviews.blob(this.shown.definitionDigest, hash));
      const url = URL.createObjectURL(blob), link = document.createElement('a');
      link.href = url; link.download = hash; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) { this.fail(e); }
  }
  async findAffected(hash: string) {
    try { this.affected = await firstValueFrom(this.reviews.affected(hash)); } catch (e) { this.fail(e); }
  }
  async revokeSelected() {
    if (this.busy || !this.comment.trim()) return;
    this.busy = true; this.error = '';
    try {
      for (const item of this.affected.filter(d => d.selected)) {
        await firstValueFrom(this.reviews.act(item.partUuid, item, 'revoke', this.comment, reviewRequestUuid()));
        item.state = 'revoked'; item.selected = false; this.changed.emit();
      }
      await this.reload(this.selectedDigest);
    } catch (e) { this.fail(e); }
    finally { this.busy = false; }
  }
  private fail(e: unknown) { this.error = (e as { error?: { error?: string } }).error?.error ?? 'Could not complete the review request. Refresh or retry.'; }
}
