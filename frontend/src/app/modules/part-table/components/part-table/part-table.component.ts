import { AfterViewInit, Component, OnDestroy, OnInit, ViewChild, ChangeDetectionStrategy } from '@angular/core';
import { lifecycleBadgeClass, lifecycleLabel, Part } from '../../types/part';
import { EMPTY_PART_FILTER, PartFilter, PartStatusFilter } from '../../types/part-filter';
import { PartDataSource } from '../../services/part.dataSource';
import { PartService } from '../../services/part.service';
import { PartNumberService } from '../../services/part-number.service';
import { OrderNumberService } from '../../services/order-number.service';
import { MatPaginator } from '@angular/material/paginator';
import { Sort } from '@angular/material/sort';
import { DeleteConfirmationDialogComponent } from '../../../../components/delete-confirmation-dialog/delete-confirmation-dialog.component';
import { MatDialog } from '@angular/material/dialog';
import { PartEditDialogComponent } from '../part-edit-dialog/part-edit-dialog.component';
import { PartDetailDialogComponent } from '../part-detail-dialog/part-detail-dialog.component';
import { DatasheetService } from '../../../datasheet-table/services/datasheet.service';
import { LibraryService } from '../../../library-table/services/library.service';
import { SymbolService } from '../../../symbol-table/services/symbol.service';
import { FootprintService } from '../../../footprint-table/services/footprint.service';
import { ManufacturerService } from '../../../manufacturer-table/services/manufacturer.service';
import { DistributorService } from '../../../distributor-table/services/distributor.service';
import { Library } from '../../../library-table/types/library';
import { Manufacturer } from '../../../manufacturer-table/types/manufacturer';
import { Distributor } from '../../../distributor-table/types/distributor';
import { Datasheet } from '../../../datasheet-table/types/datasheet';
import { Footprint } from '../../../footprint-table/types/footprint';
import { Symbol } from '../../../symbol-table/types/symbol';
import { Subscription } from 'rxjs';
import {
  RevisionHistoryDialogComponent,
  RevisionHistoryDialogData
} from '../../../history/components/revision-history-dialog/revision-history-dialog.component';
import { AuthService } from '../../../../services/auth.service';
import { isRowControlEvent } from '../../../../shared/row-click';

@Component({
    selector: 'part-table',
    templateUrl: './part-table.component.html',
    styleUrl: './part-table.component.scss',
    changeDetection: ChangeDetectionStrategy.Eager,
    standalone: false
})
export class PartTableComponent implements OnInit, AfterViewInit, OnDestroy {
  displayedColumns: string[] = ['id', 'name', 'value', 'comment', 'lifecycleStatus', 'reviewState', 'actions'];
  readonly lifecycleLabel = lifecycleLabel;
  readonly lifecycleBadgeClass = lifecycleBadgeClass;
  dataSource = new PartDataSource(this.partService, this.partNumberService, this.orderNumberService);
  manufacturerOptions: Manufacturer[] = [];
  sort: Sort = { active: 'id', direction: 'asc' };
  manufacturerList: Manufacturer[] = [];
  distributorList: Distributor[] = [];
  datasheetList: Datasheet[] = [];
  symbolList: Symbol[] = [];
  footprintList: Footprint[] = [];
  libraryList: Library[] = [];

  filter: PartFilter = { ...EMPTY_PART_FILTER };

  private allParts: Part[] = [];
  private partsSubscription?: Subscription;

  @ViewChild(MatPaginator, {static: false}) paginator!: MatPaginator;

  constructor(
    private partService: PartService,
    private partNumberService: PartNumberService,
    private orderNumberService: OrderNumberService,
    private datasheetService: DatasheetService,
    private libraryService: LibraryService,
    private symbolService: SymbolService,
    private footprintService: FootprintService,
    private manufacturerService: ManufacturerService,
    private distributorService: DistributorService,
    public dialog: MatDialog,
    public auth: AuthService
  ) {

    // Load list of libraries
    this.libraryService.getLibraries(this.sort).subscribe(libraries => {
      this.libraryList = libraries;
    });

    this.symbolService.getSymbols(this.sort).subscribe(symbols => {
      this.symbolList = symbols;
    });

    this.footprintService.getFootprints(this.sort).subscribe(footprints => {
      this.footprintList = footprints;
    });

    this.datasheetService.getDatasheets(this.sort).subscribe(datasheets => {
      this.datasheetList = datasheets;
    });

    this.manufacturerService.getManufacturers(this.sort).subscribe(manufacturers => {
      this.manufacturerList = manufacturers;
      this.manufacturerOptions = [...manufacturers].sort((a, b) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
    });

    this.distributorService.getDistributors(this.sort).subscribe(distributors => {
      this.distributorList = distributors;
    });

    // Assign the data to the data source for the table to render
    this.dataSource.loadParts(this.sort);
  }

  ngOnInit(): void {
    this.partsSubscription = this.dataSource.allRows$.subscribe(parts => {
      this.allParts = parts;
    });
  }

  ngAfterViewInit() {
    this.dataSource.paginator = this.paginator;
  }

  ngOnDestroy(): void {
    this.partsSubscription?.unsubscribe();
  }

  get hasActiveFilters(): boolean {
    return !!this.filter.search
      || this.filter.libraryId !== 'all'
      || this.filter.manufacturerId !== 'all'
      || this.filter.status !== 'all' || this.filter.reviewState !== 'all';
  }

  openHistory(row: Part, event?: Event): void {
    event?.stopPropagation();
    const data: RevisionHistoryDialogData = {
      entity: 'part',
      id: row.id,
      name: row.name
    };
    this.dialog.open(RevisionHistoryDialogComponent, {
      width: '860px',
      maxWidth: '95vw',
      data
    }).afterClosed().subscribe(changed => {
      if (changed) {
        this.dataSource.loadParts(this.sort);
      }
    });
  }

  sortData(sort: Sort) {
    this.sort = sort;
    this.dataSource.loadParts(sort);
  }

  onSearchChange(search: string) {
    this.applyFilter({ ...this.filter, search });
  }

  onLibraryChange(libraryId: number | 'all') {
    this.applyFilter({ ...this.filter, libraryId });
  }

  onManufacturerChange(manufacturerId: number | 'all') {
    this.applyFilter({ ...this.filter, manufacturerId });
  }

  onStatusChange(status: PartStatusFilter) {
    this.applyFilter({ ...this.filter, status });
  }

  onReviewChange(reviewState: string) {
    this.applyFilter({ ...this.filter, reviewState });
  }

  clearFilters() {
    this.applyFilter({ ...EMPTY_PART_FILTER });
  }

  /** Exports the currently loaded parts as CSV, mirroring the design's Export action. */
  exportCsv() {
    const header = ['ID', 'Name', 'Value', 'Comment', 'Keywords', 'Status'];
    const rows = this.allParts.map(part => [
      part.id,
      part.name,
      part.value,
      part.comment,
      part.keywords ?? '',
      lifecycleLabel(part.lifecycleStatus)
    ]);

    const csv = [header, ...rows]
      .map(row => row.map(field => `"${String(field ?? '').replace(/"/g, '""')}"`).join(','))
      .join('\n');

    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = 'parts.csv';
    link.click();
    URL.revokeObjectURL(url);
  }

  onRowClick(row: Part, event: Event): void {
    if (isRowControlEvent(event)) {
      return;
    }
    this.viewRow(row);
  }

  viewRow(row: any) {
    const dialogRef = this.dialog.open(PartDetailDialogComponent, {
      width: '70%',
      maxWidth: '72rem',
      data: { id: +row.id, ...this.referenceLists() }
    });

    dialogRef.afterClosed().subscribe(result => {
      if (result) {
        this.dataSource.loadParts(this.sort);
      }
    });
  }

  editRow(row: any) {
    const dialogRef = this.dialog.open(PartEditDialogComponent, {
      width: '92%',
      maxWidth: '72rem',
      data: { id: +row.id, ...this.referenceLists() }
    });

    dialogRef.afterClosed().subscribe((result) => {
      if (result) {
        this.dataSource.loadParts(this.sort);
      }
    });
  }

  deleteRow(row: any) {
    const dialogRef = this.dialog.open(DeleteConfirmationDialogComponent, {
      width: '28rem'
    });

    dialogRef.afterClosed().subscribe((result) => {
      if (result) {
        this.partService.deleteData(row.id).subscribe({
          next: (response) => console.log(response),
          error: (error) => console.error('Error:', error)
        });
        this.dataSource.loadParts(this.sort);
      }
    });
  }

  addEntry() {
    const dialogRef = this.dialog.open(PartEditDialogComponent, {
      width: '92%',
      maxWidth: '72rem',
      data: { id: -1, ...this.referenceLists() }
    });

    dialogRef.afterClosed().subscribe((result) => {
      if (result) {
        this.dataSource.loadParts(this.sort);
      }
    });
  }

  /** Lookup lists the part dialogs resolve their foreign keys against. */
  private referenceLists() {
    return {
      manufacturerList: this.manufacturerList,
      distributorList: this.distributorList,
      datasheetList: this.datasheetList,
      symbolList: this.symbolList,
      footprintList: this.footprintList,
      libraryList: this.libraryList,
    };
  }

  private applyFilter(filter: PartFilter) {
    this.filter = filter;
    this.dataSource.setFilter(filter);
  }
}
