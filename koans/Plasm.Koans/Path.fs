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
      L09_Relationships.lesson
      L10_Changesets.lesson
      L11_Writes.lesson
      L12_ChildWrites.lesson
      L13_Transactions.lesson
      L14_Joins.lesson
      L15_AggregatesAndGrouping.lesson
      L16_ManyToManyAndHasOne.lesson
      L17_BulkWrites.lesson
      L18_HandWrittenSql.lesson
      L19_PreparedQueries.lesson
      L20_Failures.lesson
      L21_TestingWithPlasm.lesson
      L22_QueryPipelines.lesson ]
