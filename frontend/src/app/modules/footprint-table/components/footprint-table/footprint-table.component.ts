import { AfterViewInit, Component, OnDestroy, OnInit, ViewChild, ChangeDetectionStrategy } from '@angular/core';
import { MatPaginator } from '@angular/material/paginator';
import { Sort } from '@angular/material/sort';
import { MatDialog } from '@angular/material/dialog';
import { Subscription } from 'rxjs';

import { FootprintDataSource } from '../../services/footprint.dataSource';
import { FootprintService } from '../../services/footprint.service';
import { Footprint, FootprintModel } from '../../types/footprint';
import { EMPTY_FOOTPRINT_FILTER, FootprintFilter } from '../../types/footprint-filter';
import { LibraryFile } from '../../../library-file-table/types/library-file';
import { LibraryFileService } from '../../../library-file-table/services/library-file.service';
import { uniqueSorted, YesNoFilter } from '../../../../shared/search-filter';
import { FootprintDetailDialogComponent } from '../footprint-detail-dialog/footprint-detail-dialog.component';
import {
  RevisionHistoryDialogComponent,
  RevisionHistoryDialogData
} from '../../../history/components/revision-history-dialog/revision-history-dialog.component';

@Component({
    selector: 'footprint-table',
    templateUrl: './footprint-table.component.html',
    styleUrl: './footprint-table.component.scss',
    changeDetection: ChangeDetectionStrategy.Eager,
    standalone: false
})
export class FootprintTableComponent implements OnInit, AfterViewInit, OnDestroy {
  displayedColumns: string[] = [
    'id',
    'name',
    'verification',
    'description',
    'tags',
    'libraryFileId',
    'models',
    'revisionNo',
    'boardOnly',
    'layer',
    'actions'
  ];
  dataSource = new FootprintDataSource(this.footprintService);
  sort: Sort = { active: 'id', direction: 'asc' };
  filter: FootprintFilter = { ...EMPTY_FOOTPRINT_FILTER };
  libraryFiles: LibraryFile[] = [];
  layers: string[] = [];

  private rowsSubscription?: Subscription;

  @ViewChild(MatPaginator, {static: false}) paginator!: MatPaginator;

  constructor(
    private footprintService: FootprintService,
    private libraryFileService: LibraryFileService,
    public dialog: MatDialog
  ) {
    this.libraryFileService.getLibraryFiles({ active: 'name', direction: 'asc' }).subscribe(files => {
      this.libraryFiles = files
        .filter(file => file.type === 0)
        .sort((a, b) => a.name.localeCompare(b.name));
    });
    this.dataSource.loadFootprints(this.sort);
  }

  ngOnInit(): void {
    this.rowsSubscription = this.dataSource.allRows$.subscribe(rows => {
      this.layers = uniqueSorted(rows.map(row => row.layer));
    });
  }

  ngAfterViewInit() {
    this.dataSource.paginator = this.paginator;
  }

  ngOnDestroy(): void {
    this.rowsSubscription?.unsubscribe();
  }

  sortData(sort: Sort) {
    this.sort = sort;
    this.dataSource.loadFootprints(sort);
  }

  get hasActiveFilters(): boolean {
    return !!this.filter.search.trim()
      || this.filter.libraryFileId !== 'all'
      || this.filter.boardOnly !== 'all'
      || this.filter.layer !== 'all';
  }

  onSearchChange(search: string) {
    this.applyFilter({ ...this.filter, search });
  }

  onLibraryFileChange(libraryFileId: number | 'all') {
    this.applyFilter({ ...this.filter, libraryFileId });
  }

  onBoardOnlyChange(boardOnly: YesNoFilter) {
    this.applyFilter({ ...this.filter, boardOnly });
  }

  onLayerChange(layer: string) {
    this.applyFilter({ ...this.filter, layer });
  }

  clearFilters() {
    this.applyFilter({ ...EMPTY_FOOTPRINT_FILTER });
  }

  private applyFilter(filter: FootprintFilter) {
    this.filter = filter;
    this.dataSource.setFilter(filter);
  }

  modelCount(row: Footprint): number {
    return row.models?.length ?? (row.model3dId ? 1 : 0);
  }

  modelTooltip(row: Footprint): string {
    const models = row.models ?? [];
    if (!models.length) {
      return row.model3dId ? `Model #${row.model3dId}` : 'No 3D models';
    }
    return models.map(m => this.modelPath(m)).join('\n');
  }

  modelPath(model: FootprintModel): string {
    const dir = (model.directory || '').replace(/\\/g, '/');
    const file = model.filename || '';
    if (!dir) {
      return file || '—';
    }
    return dir.endsWith('/') ? `${dir}${file}` : `${dir}/${file}`;
  }

  openDetail(row: Footprint): void {
    this.dialog.open(FootprintDetailDialogComponent, {
      width: '900px',
      maxWidth: '95vw',
      data: { id: row.id }
    });
  }

  openHistory(row: Footprint, event?: Event): void {
    event?.stopPropagation();
    const data: RevisionHistoryDialogData = {
      entity: 'footprint',
      id: row.id,
      name: row.name
    };
    this.dialog.open(RevisionHistoryDialogComponent, {
      width: '860px',
      maxWidth: '95vw',
      data
    }).afterClosed().subscribe(changed => {
      if (changed) {
        this.dataSource.loadFootprints(this.sort);
      }
    });
  }
}
