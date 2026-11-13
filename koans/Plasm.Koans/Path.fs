/// The lessons, in the order the path walks them.
module Plasm.Koans.Path

open Plasm.Koans.Lessons

let lessons: Lesson list =
    [ L01_RecordsAndSchemas.lesson
      L02_QueryValues.lesson
      L03_FiltersAndOrdering.lesson
      L04_Reading.lesson
      L05_Relationships.lesson
      L06_Changesets.lesson
      L07_Writes.lesson
      L08_ChildWrites.lesson
      L09_Transactions.lesson
      L10_Joins.lesson
      L11_AggregatesAndGrouping.lesson
      L12_ManyToManyAndHasOne.lesson
      L13_BulkWrites.lesson
      L14_HandWrittenSql.lesson
      L15_PreparedQueries.lesson
      L16_Failures.lesson
      L17_TestingWithPlasm.lesson
      L18_QueryPipelines.lesson ]
