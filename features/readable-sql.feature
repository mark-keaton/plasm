# Executable acceptance specification (Reqnroll): the SQL Plasm sends reads as
# a person would write it. Written before the implementation, from the
# comparison of Plasm's SQL with EF Core's (2026-11-17).
@implemented
Feature: The SQL a query sends reads as a person would write it
  As an F# application developer reading a log
  I want the SQL Plasm sends to say what it does and no more
  So that I can read a slow query's text and know which F# query it is

  A column is read under its own name, with no alias repeating it; a record
  selected names its columns after its fields. Conditions joined by AND or OR
  are written as one list, with parentheses only where precedence needs them
  (an OR inside an AND, an operation inside another). An ascending order is not
  marked; a negated equality of required values is `<>`; an if, elif and else is one
  CASE;
  a derived table has its own name, never the name of the table inside it. A
  statement names its table once, and a NULL written is written, not sent.
  None of this changes what a query reads or writes.

  # Customer = { Id: CustomerId; Name: string; [<Unique>] Email: string }
  # Order = { Id: OrderId; CustomerId: CustomerId option; Status: OrderStatus; Total: decimal; CancellationReason: string option; ... }
  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: A read says what it reads and no more

    Scenario: Columns under their own names, conditions in one list, ascending unmarked
      When I explain the Customers named "Ada" whose email starts with "a", other than 3, by name and then email descending, taking 10
      Then the SQL is:
        """
        SELECT "t0"."id", "t0"."name", "t0"."email" FROM "customers" AS "t0" WHERE "t0"."name" = @p0 AND "t0"."email" LIKE @p1 AND "t0"."id" <> @p2 ORDER BY "t0"."name", "t0"."email" DESC, "t0"."id" LIMIT @p3
        """

    Scenario: An OR inside an AND keeps its parentheses, and a negated equality is <>
      When I explain the ids and totals of the Orders over 10 or under 2 that are not drafts
      Then the SQL is:
        """
        SELECT "t0"."id" AS "c0", "t0"."total" AS "c1" FROM "orders" AS "t0" WHERE ("t0"."total" > @p0 OR "t0"."total" < @p1) AND "t0"."status" <> @p2
        """

    Scenario: A record selected names its columns after its fields
      When I explain the Orders joined with their Customers, selecting a record of the total and the name
      Then the SQL is:
        """
        SELECT "j1"."name" AS "Name", "t0"."total" AS "Total" FROM "orders" AS "t0" INNER JOIN "customers" AS "j1" ON "t0"."customer_id" = "j1"."id"
        """

    Scenario: An operation inside a comparison needs no parentheses
      When I explain the Orders without a cancellation reason whose doubled total is over 10
      Then the SQL is:
        """
        SELECT "t0"."id", "t0"."customer_id", "t0"."status", "t0"."total", "t0"."cancellation_reason", "t0"."placed_at", "t0"."version" FROM "orders" AS "t0" WHERE "t0"."cancellation_reason" IS NULL AND "t0"."total" * @p0 > @p1
        """

    Scenario: A derived table has a name of its own
      When I explain the Orders by total, binding their two newest Payments
      Then the Payments load's SQL is:
        """
        SELECT "ranked"."id", "ranked"."order_id", "ranked"."amount", "ranked"."status", "ranked"."created_at" FROM (SELECT "t0"."id", "t0"."order_id", "t0"."amount", "t0"."status", "t0"."created_at", ROW_NUMBER() OVER (PARTITION BY "t0"."order_id" ORDER BY "t0"."created_at" DESC, "t0"."id") AS "__rn" FROM "payments" AS "t0" WHERE "t0"."order_id" = ANY(@p0)) AS "ranked" WHERE "ranked"."__rn" <= @p1 ORDER BY "ranked"."created_at" DESC, "ranked"."id"
        """

    Scenario: An if, elif and else is one CASE
      When I explain the Orders' sizes in words, through if, elif and else on the total
      Then the SQL is:
        """
        SELECT CASE WHEN "t0"."total" > @p0 THEN 'big' WHEN "t0"."total" > @p1 THEN 'medium' ELSE 'small' END AS "c0" FROM "orders" AS "t0"
        """

  Rule: A write names its table once

    Scenario: An update and a delete name their table once
      Given a Customer "Ada" with a published Order of 10
      When I change the Order to cancelled, then delete it
      Then the update begins "UPDATE "orders" SET " and has no alias
      And the delete is "DELETE FROM "orders" WHERE "orders"."id" = @p0 AND "orders"."version" = @p1"
