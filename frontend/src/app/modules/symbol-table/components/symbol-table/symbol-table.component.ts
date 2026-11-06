import { AfterViewInit, Component, ViewChild, ChangeDetectionStrategy } from '@angular/core';
import { MatPaginator } from '@angular/material/paginator';
import { Sort } from '@angular/material/sort';
import { MatDialog } from '@angular/material/dialog';

import { SymbolDataSource } from '../../services/symbol.dataSource';
import { SymbolService } from '../../services/symbol.service';
import { Symbol } from '../../types/symbol';
import { EMPTY_SYMBOL_FILTER, SymbolFilter } from '../../types/symbol-filter';
import { LibraryFile } from '../../../library-file-table/types/library-file';
import { LibraryFileService } from '../../../library-file-table/services/library-file.service';
import { YesNoFilter } from '../../../../shared/search-filter';
import {
  RevisionHistoryDialogComponent,
  RevisionHistoryDialogData
} from '../../../history/components/revision-history-dialog/revision-history-dialog.component';

@Component({
    selector: 'symbol-table',
    templateUrl: './symbol-table.component.html',
    styleUrl: './symbol-table.component.scss',
    changeDetection: ChangeDetectionStrategy.Eager,
    standalone: false
})
export class SymbolTableComponent implements AfterViewInit {
  displayedColumns: string[] = ['id', 'name', 'verification', 'description', 'libraryFileId', 'revisionNo', 'inBom', 'actions'];
  dataSource = new SymbolDataSource(this.symbolService);
  sort: Sort = { active: 'id', direction: 'asc' };
  filter: SymbolFilter = { ...EMPTY_SYMBOL_FILTER };
  libraryFiles: LibraryFile[] = [];

  @ViewChild(MatPaginator, {static: false}) paginator!: MatPaginator;

  constructor(
    private symbolService: SymbolService,
    private libraryFileService: LibraryFileService,
    public dialog: MatDialog
  ) {
    this.libraryFileService.getLibraryFiles({ active: 'name', direction: 'asc' }).subscribe(files => {
      this.libraryFiles = files
        .filter(file => file.type === 1)
        .sort((a, b) => a.name.localeCompare(b.name));
    });
    this.dataSource.loadSymbols(this.sort);
  }

  ngAfterViewInit() {
    this.dataSource.paginator = this.paginator;
  }

  sortData(sort: Sort) {
    this.sort = sort;
    this.dataSource.loadSymbols(sort);
  }

  get hasActiveFilters(): boolean {
    return !!this.filter.search.trim()
      || this.filter.libraryFileId !== 'all'
      || this.filter.inBom !== 'all';
  }

  onSearchChange(search: string) {
    this.applyFilter({ ...this.filter, search });
  }

  onLibraryFileChange(libraryFileId: number | 'all') {
    this.applyFilter({ ...this.filter, libraryFileId });
  }

  onInBomChange(inBom: YesNoFilter) {
    this.applyFilter({ ...this.filter, inBom });
  }

  clearFilters() {
    this.applyFilter({ ...EMPTY_SYMBOL_FILTER });
  }

  private applyFilter(filter: SymbolFilter) {
    this.filter = filter;
    this.dataSource.setFilter(filter);
  }

  openHistory(row: Symbol, event?: Event): void {
    event?.stopPropagation();
    const data: RevisionHistoryDialogData = {
      entity: 'symbol',
      id: row.id,
      name: row.name
    };
    this.dialog.open(RevisionHistoryDialogComponent, {
      width: '860px',
      maxWidth: '95vw',
      data
    }).afterClosed().subscribe(changed => {
      if (changed) {
        this.dataSource.loadSymbols(this.sort);
      }
    });
  }
}
