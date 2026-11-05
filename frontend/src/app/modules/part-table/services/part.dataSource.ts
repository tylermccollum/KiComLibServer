import { Injectable } from "@angular/core";
import { Part } from "../types/part";
import { PartNumber } from "../types/part-number";
import { OrderNumber } from "../types/order-number";
import { EMPTY_PART_FILTER, PartFilter } from "../types/part-filter";
import { PartService } from "./part.service";
import { PartNumberService } from "./part-number.service";
import { OrderNumberService } from "./order-number.service";
import { Sort } from "@angular/material/sort";
import { PagedDataSource } from "../../../shared/paged-data-source";
import { forkJoin, of } from "rxjs";
import { catchError } from "rxjs/operators";

@Injectable()
export class PartDataSource extends PagedDataSource<Part> {
  private filter: PartFilter = { ...EMPTY_PART_FILTER };
  /** Manufacturer ids attached to a part through its part numbers. */
  private manufacturerIdsByPart = new Map<number, Set<number>>();
  /** Manufacturer part numbers attached to a part, used by the text search. */
  private manufacturerPartNumbersByPart = new Map<number, string[]>();
  /** Distributor order numbers attached to a part, used by the text search. */
  private distributorPartNumbersByPart = new Map<number, string[]>();

  constructor(
    private partService: PartService,
    private partNumberService: PartNumberService,
    private orderNumberService: OrderNumberService
  ) {
    super();
  }

  loadParts(sort: Sort): void {
    this.isLoading$.next(true);
    forkJoin({
      parts: this.partService.getParts(sort),
      partNumbers: this.partNumberService.getPartNumbers({ active: 'id', direction: 'asc' }).pipe(
        catchError(() => of([] as PartNumber[]))
      ),
      orderNumbers: this.orderNumberService.getOrderNumbers({ active: 'id', direction: 'asc' }).pipe(
        catchError(() => of([] as OrderNumber[]))
      )
    }).subscribe(({ parts, partNumbers, orderNumbers }) => {
      const indexed = indexPartNumbers(partNumbers);
      this.manufacturerIdsByPart = indexed.manufacturers;
      this.manufacturerPartNumbersByPart = indexed.manufacturerPartNumbers;
      this.distributorPartNumbersByPart = indexDistributorPartNumbers(partNumbers, orderNumbers);
      this.setRows(parts);
      this.isLoading$.next(false);
    });
  }

  setFilter(filter: PartFilter): void {
    this.filter = filter;
    this.refresh({ resetPage: true });
  }

  protected override applyFilter(parts: Part[]): Part[] {
    const search = this.filter.search.trim().toLowerCase();

    return parts.filter(part => {
      const matchesSearch = !search
        || [part.name, part.value, part.comment, part.keywords]
          .some(field => (field ?? '').toLowerCase().includes(search))
        || (this.manufacturerPartNumbersByPart.get(part.id) ?? [])
          .some(mpn => mpn.toLowerCase().includes(search))
        || (this.distributorPartNumbersByPart.get(part.id) ?? [])
          .some(number => number.toLowerCase().includes(search));

      const matchesLibrary = this.filter.libraryId === 'all'
        || part.libraryId === this.filter.libraryId;

      const matchesManufacturer = this.filter.manufacturerId === 'all'
        || (this.manufacturerIdsByPart.get(part.id)?.has(this.filter.manufacturerId) ?? false);

      const matchesStatus = this.filter.status === 'all'
        || part.lifecycleStatus === this.filter.status;

      const matchesReview = !this.filter.reviewState || this.filter.reviewState === 'all' || part.reviewState === this.filter.reviewState;
      return matchesSearch && matchesLibrary && matchesManufacturer && matchesStatus && matchesReview;
    });
  }
}

interface PartNumberIndex {
  manufacturers: Map<number, Set<number>>;
  manufacturerPartNumbers: Map<number, string[]>;
}

function indexPartNumbers(partNumbers: PartNumber[]): PartNumberIndex {
  const manufacturers = new Map<number, Set<number>>();
  const manufacturerPartNumbers = new Map<number, string[]>();

  for (const partNumber of partNumbers) {
    if (partNumber.partId == null) {
      continue;
    }

    if (partNumber.manufacturerId != null) {
      let ids = manufacturers.get(partNumber.partId);
      if (!ids) {
        ids = new Set<number>();
        manufacturers.set(partNumber.partId, ids);
      }
      ids.add(partNumber.manufacturerId);
    }

    const mpn = partNumber.manufacturerPartNumber?.trim();
    if (mpn) {
      let numbers = manufacturerPartNumbers.get(partNumber.partId);
      if (!numbers) {
        numbers = [];
        manufacturerPartNumbers.set(partNumber.partId, numbers);
      }
      numbers.push(mpn);
    }
  }

  return { manufacturers, manufacturerPartNumbers };
}

function indexDistributorPartNumbers(
  partNumbers: PartNumber[],
  orderNumbers: OrderNumber[]
): Map<number, string[]> {
  const partIdByPartNumber = new Map<number, number>();
  for (const partNumber of partNumbers) {
    if (partNumber.id != null && partNumber.partId != null) {
      partIdByPartNumber.set(partNumber.id, partNumber.partId);
    }
  }

  const byPart = new Map<number, string[]>();
  for (const orderNumber of orderNumbers) {
    const partId = partIdByPartNumber.get(orderNumber.partNumberId);
    const value = orderNumber.orderNumberValue?.trim();
    if (partId == null || !value) {
      continue;
    }
    let numbers = byPart.get(partId);
    if (!numbers) {
      numbers = [];
      byPart.set(partId, numbers);
    }
    numbers.push(value);
  }
  return byPart;
}
