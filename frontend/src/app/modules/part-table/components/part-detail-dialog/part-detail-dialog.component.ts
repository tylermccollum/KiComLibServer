import { Component, Inject, ChangeDetectionStrategy } from '@angular/core';
import { Router } from '@angular/router';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { Sort } from '@angular/material/sort';
import { catchError, forkJoin, Observable, of, switchMap } from 'rxjs';

import { PartService } from '../../services/part.service';
import { PartEditDialogComponent, PartEditDialogData } from '../part-edit-dialog/part-edit-dialog.component';
import { PartNumberService } from '../../services/part-number.service';
import { OrderNumberService } from '../../services/order-number.service';
import { SymbolService } from '../../../symbol-table/services/symbol.service';
import { FootprintService } from '../../../footprint-table/services/footprint.service';
import { LibraryService } from '../../../library-table/services/library.service';
import { DatasheetService } from '../../../datasheet-table/services/datasheet.service';
import { ManufacturerService } from '../../../manufacturer-table/services/manufacturer.service';
import { DistributorService } from '../../../distributor-table/services/distributor.service';
import { parseSvgPreview } from '../../services/svg-preview';

import { AuthService } from '../../../../services/auth.service';
import { ProjectService } from '../../../projects/services/project.service';
import { PartUsage } from '../../../projects/types/project';
import { lifecycleBadgeClass, lifecycleLabel, Part, PartLink } from '../../types/part';
import { Manufacturer } from '../../../manufacturer-table/types/manufacturer';
import { Distributor } from '../../../distributor-table/types/distributor';
import { Datasheet } from '../../../datasheet-table/types/datasheet';
import { Symbol } from '../../../symbol-table/types/symbol';
import { Footprint } from '../../../footprint-table/types/footprint';
import { Library } from '../../../library-table/types/library';

/** One distributor offer, flattened from part number + order number for display. */
export interface OrderRow {
  manufacturer: string;
  manufacturerPartNumber: string;
  packageType: string;
  distributor: string;
  distributorUrl: string;
  orderNumber: string;
  quantityMin: number;
  quantitySteps: number;
}

export interface PartDetailDialogData {
  id: number;
  manufacturerList: Manufacturer[];
  distributorList: Distributor[];
  datasheetList: Datasheet[];
  symbolList: Symbol[];
  footprintList: Footprint[];
  libraryList: Library[];
}

@Component({
    selector: 'part-detail-dialog',
    templateUrl: './part-detail-dialog.component.html',
    styleUrl: './part-detail-dialog.component.scss',
    changeDetection: ChangeDetectionStrategy.Eager,
    standalone: false
})
export class PartDetailDialogComponent {
  private readonly sort: Sort = { active: 'id', direction: 'asc' };
  readonly lifecycleLabel = lifecycleLabel;
  readonly lifecycleBadgeClass = lifecycleBadgeClass;

  part?: Part;
  orderRows: OrderRow[] = [];
  isLoadingOrders = true;

  symbolPreview: SafeHtml = '';
  symbolPreviewViewBox = '0 0 1 1';
  footprintPreview: SafeHtml = '';
  footprintPreviewViewBox = '0 0 1 1';

  readonly orderColumns = [
    'manufacturer',
    'manufacturerPartNumber',
    'distributor',
    'orderNumber',
    'quantityMin',
    'quantitySteps'
  ];

  catalog: Part[] = [];
  selectedAlternatives: Part[] = [];
  readonly alternativeColumns = ['name', 'value', 'comment'];

  signedIn = false;
  catalogChanged = false;
  usages: PartUsage[] = [];
  usageError = '';
  usageLoading = false;
  readonly usageColumns = ['project', 'revision', 'quantity'];

  constructor(
    public dialogRef: MatDialogRef<PartDetailDialogComponent>,
    private partService: PartService,
    private partNumberService: PartNumberService,
    private orderNumberService: OrderNumberService,
    private symbolService: SymbolService,
    private footprintService: FootprintService,
    private libraryService: LibraryService,
    private datasheetService: DatasheetService,
    private manufacturerService: ManufacturerService,
    private distributorService: DistributorService,
    private sanitizer: DomSanitizer,
    public auth: AuthService,
    private dialog: MatDialog,
    private projects: ProjectService,
    private router: Router,
    @Inject(MAT_DIALOG_DATA) public data: PartDetailDialogData
  ) {
    this.partService.getParts(this.sort).subscribe({
      next: parts => {
        const known = new Map(this.catalog.map(item => [item.id, item]));
        for (const item of parts) {
          known.set(item.id, item);
        }
        this.catalog = [...known.values()];
        this.refreshSelectedAlternatives();
      },
      error: error => console.error('Error:', error)
    });

    this.partService.getPart(data.id).subscribe(part => {
      this.part = part;
      this.refreshSelectedAlternatives();
      this.loadPreviews(part);
    });

    this.ensureReferenceLists();
    this.signedIn = this.auth.profile().authenticated;
    if (this.signedIn) {
      this.loadUsage(data.id);
    }
  }

  openProject(row: PartUsage): void {
    this.dialogRef.close(this.catalogChanged);
    void this.router.navigate(['/projects', row.projectId]);
  }

  whereUsed(row: PartUsage): string {
    return [row.revision, row.release].filter(part => !!part).join(' / ') || '—';
  }

  get productImageUrl(): string | null {
    return this.part?.imageHash ? this.partService.partImageUrl(this.data.id) : null;
  }

  get libraryName(): string {
    return this.lookup(this.data.libraryList, this.part?.libraryId)?.name ?? '—';
  }

  get symbolName(): string {
    return this.lookup(this.data.symbolList, this.part?.symbolId)?.name ?? '';
  }

  get footprintName(): string {
    return this.lookup(this.data.footprintList, this.part?.footprintId)?.name ?? '';
  }

  get partLinks(): PartLink[] {
    return (this.part?.links ?? []).filter(link => !!link.url);
  }

  get datasheets(): Datasheet[] {
    return this.datasheetIds.map(id =>
      this.lookup(this.data.datasheetList, id)
      ?? { id, uuid: '', filename: `Datasheet #${id}`, directory: '', fileSize: 0, comment: null, updatedAt: '', createdAt: '' });
  }

  datasheetOpenUrl(sheet: Datasheet): string {
    return this.datasheetService.fileUrl(sheet.id);
  }

  datasheetDownloadUrl(sheet: Datasheet): string {
    return this.datasheetService.fileUrl(sheet.id, true);
  }

  private get datasheetIds(): number[] {
    const listed = (this.part?.datasheetIds ?? []).filter(id => id > 0);
    const source = listed.length ? listed : (this.part?.datasheetId ? [this.part.datasheetId] : []);
    return source.filter((id, index) => source.indexOf(id) === index);
  }

  close(): void {
    this.dialogRef.close(this.catalogChanged);
  }

  /** Opens the editor for this part and refreshes the view after a successful save. */
  edit(): void {
    const source = this.part;
    if (!source || !this.auth.canWrite()) {
      return;
    }

    const data: PartEditDialogData = {
      id: source.id,
      manufacturerList: this.data.manufacturerList,
      distributorList: this.data.distributorList,
      datasheetList: this.data.datasheetList,
      symbolList: this.data.symbolList,
      footprintList: this.data.footprintList,
      libraryList: this.data.libraryList
    };

    this.dialog.open(PartEditDialogComponent, {
      width: '92%',
      maxWidth: '72rem',
      data
    }).afterClosed().subscribe(savedId => {
      if (typeof savedId !== 'number' || savedId <= 0) {
        return;
      }

      this.catalogChanged = true;
      this.reloadPart();
    });
  }

  /** Opens a new part with symbol, footprint, description and library copied from this part. */
  createAlternative(): void {
    const source = this.part;
    if (!source || !this.auth.canWrite()) {
      return;
    }

    const data: PartEditDialogData = {
      id: -1,
      manufacturerList: this.data.manufacturerList,
      distributorList: this.data.distributorList,
      datasheetList: this.data.datasheetList,
      symbolList: this.data.symbolList,
      footprintList: this.data.footprintList,
      libraryList: this.data.libraryList,
      alternativeOfId: source.id,
      preset: {
        comment: source.comment,
        libraryId: source.libraryId,
        symbolId: source.symbolId,
        footprintId: source.footprintId
      }
    };

    this.dialog.open(PartEditDialogComponent, {
      width: '92%',
      maxWidth: '72rem',
      data
    }).afterClosed().subscribe(createdId => {
      if (typeof createdId !== 'number' || createdId <= 0) {
        return;
      }

      this.catalogChanged = true;
      this.refreshAfterAlternative(source.id, createdId);
    });
  }

  /**
   * Callers such as the bill of materials open this dialog with only a part id.
   * Names for library, symbol, footprint, datasheet, manufacturer and distributor
   * are resolved from these lists, so load any list that was not provided.
   */
  private ensureReferenceLists(): void {
    const data = this.data;
    const missing = !data.libraryList.length
      || !data.symbolList.length
      || !data.footprintList.length
      || !data.datasheetList.length
      || !data.manufacturerList.length
      || !data.distributorList.length;

    if (!missing) {
      this.loadOrderRows(data.id);
      return;
    }

    const keep = <T>(current: T[], load: Observable<T[]>): Observable<T[]> =>
      current.length ? of(current) : load.pipe(catchError(() => of(current)));

    forkJoin({
      libraryList: keep(data.libraryList, this.libraryService.getLibraries(this.sort)),
      symbolList: keep(data.symbolList, this.symbolService.getSymbols(this.sort)),
      footprintList: keep(data.footprintList, this.footprintService.getFootprints(this.sort)),
      datasheetList: keep(data.datasheetList, this.datasheetService.getDatasheets(this.sort)),
      manufacturerList: keep(data.manufacturerList, this.manufacturerService.getManufacturers(this.sort)),
      distributorList: keep(data.distributorList, this.distributorService.getDistributors(this.sort))
    }).subscribe({
      next: lists => {
        this.data.libraryList = lists.libraryList;
        this.data.symbolList = lists.symbolList;
        this.data.footprintList = lists.footprintList;
        this.data.datasheetList = lists.datasheetList;
        this.data.manufacturerList = lists.manufacturerList;
        this.data.distributorList = lists.distributorList;
        this.loadOrderRows(data.id);
      },
      error: error => {
        console.error('Error:', error);
        this.loadOrderRows(data.id);
      }
    });
  }

  /**
   * Resolves the part's manufacturer part numbers and, for each of them, the
   * distributor order numbers, then flattens the pairs into one row per offer.
   */
  private loadOrderRows(partId: number): void {
    this.partNumberService.getPartNumbers(this.sort, partId).pipe(
      switchMap(partNumbers => {
        if (!partNumbers.length) {
          return of([] as OrderRow[]);
        }

        return forkJoin(
          partNumbers.map(partNumber =>
            this.orderNumberService.getOrderNumbers(this.sort, partNumber.id).pipe(
              switchMap(orderNumbers => {
                const manufacturer = this.lookup(this.data.manufacturerList, partNumber.manufacturerId)?.name ?? '—';

                if (!orderNumbers.length) {
                  return of([{
                    manufacturer,
                    manufacturerPartNumber: partNumber.manufacturerPartNumber,
                    packageType: partNumber.packageType,
                    distributor: '—',
                    distributorUrl: '',
                    orderNumber: '—',
                    quantityMin: 0,
                    quantitySteps: 0
                  } as OrderRow]);
                }

                return of(orderNumbers.map(orderNumber => {
                  const distributor = this.lookup(this.data.distributorList, orderNumber.distributorId);

                  return {
                    manufacturer,
                    manufacturerPartNumber: partNumber.manufacturerPartNumber,
                    packageType: partNumber.packageType,
                    distributor: distributor?.name ?? '—',
                    distributorUrl: distributor?.url ?? '',
                    orderNumber: orderNumber.orderNumberValue,
                    quantityMin: orderNumber.quantityMin,
                    quantitySteps: orderNumber.quantitySteps
                  } as OrderRow;
                }));
              })
            )
          )
        ).pipe(switchMap(groups => of(groups.flat())));
      })
    ).subscribe({
      next: rows => {
        this.orderRows = rows;
        this.isLoadingOrders = false;
      },
      error: error => {
        console.error('Error:', error);
        this.isLoadingOrders = false;
      }
    });
  }

  private reloadPart(): void {
    this.isLoadingOrders = true;
    this.partService.getPart(this.data.id).subscribe({
      next: part => {
        this.part = part;
        this.refreshSelectedAlternatives();
        this.loadPreviews(part);
      },
      error: error => console.error('Error:', error)
    });
    this.loadOrderRows(this.data.id);
  }

  private loadPreviews(part: Part): void {
    if (part.symbolId) {
      this.symbolService.getSymbolPreview(part.symbolId).subscribe(raw => {
        const preview = parseSvgPreview(raw);
        this.symbolPreviewViewBox = preview.viewBox;
        this.symbolPreview = this.sanitizer.bypassSecurityTrustHtml(preview.content);
      });
    } else {
      this.symbolPreview = '';
      this.symbolPreviewViewBox = '0 0 1 1';
    }

    if (part.footprintId) {
      this.footprintService.getFootprintPreview(part.footprintId).subscribe(raw => {
        const preview = parseSvgPreview(raw);
        this.footprintPreviewViewBox = preview.viewBox;
        this.footprintPreview = this.sanitizer.bypassSecurityTrustHtml(preview.content);
      });
    } else {
      this.footprintPreview = '';
      this.footprintPreviewViewBox = '0 0 1 1';
    }
  }

  private lookup<T extends { id: number }>(list: T[], id: number | undefined): T | undefined {
    return id === undefined ? undefined : list.find(entry => entry.id === id);
  }

  private loadUsage(partId: number): void {
    this.usageLoading = true;
    this.projects.usage(partId).subscribe({
      next: rows => {
        this.usages = rows;
        this.usageLoading = false;
      },
      error: () => {
        this.usageError = 'Could not load projects that use this part.';
        this.usageLoading = false;
      }
    });
  }

  private linkedAlternativeIds(): number[] {
    return (this.part?.alternatives ?? []).filter(id => id !== this.part?.id);
  }

  private refreshSelectedAlternatives(): void {
    const ids = this.linkedAlternativeIds();
    this.selectedAlternatives = ids.map(id =>
      this.catalog.find(candidate => candidate.id === id)
      ?? { id, name: `Part #${id}`, value: '', comment: '' } as Part);
  }

  private refreshAfterAlternative(originalId: number, createdId: number): void {
    forkJoin({
      original: this.partService.getPart(originalId),
      created: this.partService.getPart(createdId)
    }).subscribe({
      next: ({ original, created }) => {
        this.part = original;
        const known = new Map(this.catalog.map(item => [item.id, item]));
        known.set(created.id, created);
        this.catalog = [...known.values()];
        this.refreshSelectedAlternatives();
      },
      error: error => console.error('Error:', error)
    });
  }
}
