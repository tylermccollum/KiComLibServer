import { PartReviewComponent } from './components/part-review/part-review.component';
import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatSortModule } from '@angular/material/sort';
import { FormsModule } from '@angular/forms';
import { MatTableModule } from '@angular/material/table';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { PartTableComponent } from './components/part-table/part-table.component';
import { PartService } from './services/part.service';
import { PartDataSource } from './services/part.dataSource';
import { PartEditDialogComponent } from './components/part-edit-dialog/part-edit-dialog.component';
import { PartDetailDialogComponent } from './components/part-detail-dialog/part-detail-dialog.component';
import { DistributorPickDialogComponent } from './components/distributor-pick-dialog/distributor-pick-dialog.component';
import { DatasheetDownloadDialogComponent } from './components/datasheet-download-dialog/datasheet-download-dialog.component';
import { ErrorNoticeDialogComponent } from './components/error-notice-dialog/error-notice-dialog.component';
import { MatTabsModule } from '@angular/material/tabs';
import { MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { PartNumberService } from './services/part-number.service';
import { OrderNumberService } from './services/order-number.service';
import { PartNumberDataSource } from './services/part-number.dataSource';
import { OrderNumberDataSource } from './services/order-number.dataSource';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { SearchableSelectComponent } from './components/searchable-select/searchable-select.component';
import { RouterModule } from '@angular/router';
import { TrimInputDirective } from '../../directives/trim-input.directive';
import { MatSelectModule } from '@angular/material/select';
import { AppHeaderComponent } from '../../components/app-header/app-header.component';
import { ProductImageComponent } from '../../components/product-image/product-image.component';
import { HistoryModule } from '../history/history.module';




@NgModule({
  declarations: [
    PartTableComponent,
    PartEditDialogComponent,
    PartDetailDialogComponent,
    DistributorPickDialogComponent,
    DatasheetDownloadDialogComponent,
    ErrorNoticeDialogComponent,
    SearchableSelectComponent
  ],
  imports: [
    CommonModule,
    PartReviewComponent,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatTableModule,
    MatSortModule,
    MatPaginatorModule,
    MatButtonModule,
    MatDialogModule,
    MatIconModule,
    MatAutocompleteModule,
    MatSelectModule,
    TrimInputDirective,
    RouterModule,
    MatTabsModule,
    AppHeaderComponent,
    ProductImageComponent,
    HistoryModule
],
  exports: [
    PartTableComponent,
    PartDetailDialogComponent
  ],
  providers: [
    PartService,
    PartDataSource,
    PartNumberService,
    PartNumberDataSource,
    OrderNumberService,
    OrderNumberDataSource
  ]
})
export class PartTableModule { }

export { PartDetailDialogComponent };
