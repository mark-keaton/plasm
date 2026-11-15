/// The lessons, in the order the path walks them.
module Plasm.Koans.Path

open Plasm.Koans.Lessons

let lessons: Lesson list =
    [ L01_GettingStarted.lesson
      L02_WhatARepoIs.lesson
      L03_MeetTheShop.lesson
      L04_PlasmsPromises.lesson
      L05_RecordsAndSchemas.lesson
      L06_QueryValues.lesson
      L07_FiltersAndOrdering.lesson
      L08_Reading.lesson
      L09_AggregatesAndReferences.lesson
      L10_ConstructorsAndConstraints.lesson
      L11_Writes.lesson
      L12_WritingAnAggregate.lesson
      L13_Transactions.lesson
      L14_Joins.lesson
      L15_AggregatesAndGrouping.lesson
      L16_LinksAndOwnedRecords.lesson
      L17_BulkWrites.lesson
      L18_HandWrittenSql.lesson
      L19_PreparedQueries.lesson
      L20_Failures.lesson
      L21_TestingWithPlasm.lesson
      L22_UnionsWithFields.lesson ]
