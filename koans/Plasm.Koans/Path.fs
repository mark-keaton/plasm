/// The lessons, in the order the path walks them.
module Plasm.Koans.Path

open Plasm.Koans.Lessons

let lessons: Lesson list =
    [ L01_GettingStarted.lesson
      L02_WhatARepoIs.lesson
      L03_MeetTheShop.lesson
      L04_PlasmsPromises.lesson
      L05_StoredRecords.lesson
      L06_QueryValues.lesson
      L07_FiltersAndOrdering.lesson
      L08_Reading.lesson
      L09_AggregatesAndReferences.lesson
      L10_Writes.lesson
      L11_ConstructorsAndConstraints.lesson
      L12_WritingAnAggregate.lesson
      L13_Transactions.lesson
      L14_Joins.lesson
      L15_Loads.lesson
      L16_AggregatesAndGrouping.lesson
      L17_LinksAndOwnedRecords.lesson
      L18_BulkWrites.lesson
      L19_HandWrittenSql.lesson
      L20_PreparedQueries.lesson
      L21_Failures.lesson
      L22_TestingWithPlasm.lesson
      L23_UnionsWithFields.lesson
      L24_SetOperations.lesson
      L25_WindowsAndRowAggregates.lesson ]
