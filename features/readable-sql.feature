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
        SELECT "t0"."id", "t0"."name", "t0"."email" FROM "customers" AS "t0" WHERE "t0"."name" = @p0 AND "t0"."email" LIKE @p1 AND "t0"."id" <> @p2 ORDER BY "t0"."name", "t0"."email" DESC LIMIT @p3
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
        SELECT "ranked"."id", "ranked"."order_id", "ranked"."amount", "ranked"."status", "ranked"."created_at" FROM (SELECT "t0"."id", "t0"."order_id", "t0"."amount", "t0"."status", "t0"."created_at", ROW_NUMBER() OVER (PARTITION BY "t0"."order_id" ORDER BY "t0"."created_at" DESC, "t0"."id") AS "__rn" FROM "payments" AS "t0" WHERE "t0"."order_id" = ANY(@p0)) AS "ranked" WHERE "ranked"."__rn" <= @p1 ORDER BY "ranked"."__rn"
        """

    Scenario: An if, elif and else is one CASE
      When I explain the Orders' sizes in words, through if, elif and else on the total
      Then the SQL is:
        """
        SELECT CASE WHEN "t0"."total" > @p0 THEN 'big' WHEN "t0"."total" > @p1 THEN 'medium' ELSE 'small' END AS "c0" FROM "orders" AS "t0"
        """

    Scenario: Every line meeting a condition is no line failing it, written plainly
      When I explain the Orders all of whose lines cost more than 5
      Then the SQL ends "WHERE NOT EXISTS (SELECT 1 FROM "order_lines" AS "t1" WHERE "t1"."order_id" = "t0"."id" AND "t1"."unit_price" <= @p0)"

  Rule: A write names its table once

    Scenario: An update and a delete name their table once
      Given a Customer "Ada" with a published Order of 10
      When I change the Order to cancelled, then delete it
      Then the update begins "UPDATE "orders" SET " and has no alias
      And the delete is "DELETE FROM "orders" WHERE "orders"."id" = @p0 AND "orders"."version" = @p1"

  Rule: Plasm adds nothing the result does not need

    Plasm's own additions cost the database nothing the result does not need:
    `repo.One` reads two rows to tell a second match, in no order, since any two
    tell it; a key is appended to an ordering to make pages stable only where it
    can break a tie; a load by the key of the record it reads is not sorted, and
    a load's page keeps each record's order without sorting them all again.
    Plasm's own numbers, such as that 2 or a version's increment, are written,
    not sent; a value of the application's is always sent.

    Scenario: repo.One reads at most two rows, in no order
      Given a Customer "Ada" with a published Order of 10
      When I read the one Customer named "Ada"
      Then the command is:
        """
        SELECT "t0"."id", "t0"."name", "t0"."email" FROM "customers" AS "t0" WHERE "t0"."name" = @p0 LIMIT 2
        """

    Scenario: Asking whether any row exists reads one, written as 1
      Given a Customer "Ada" with a published Order of 10
      When I ask whether a Customer is named "Ada"
      Then the command is:
        """
        SELECT 1 AS "c0" FROM "customers" AS "t0" WHERE "t0"."name" = @p0 LIMIT 1
        """

    Scenario: A unique column is a tie-break of its own
      When I explain a page of the Customers by email
      Then the SQL is:
        """
        SELECT "t0"."id", "t0"."name", "t0"."email" FROM "customers" AS "t0" ORDER BY "t0"."email" LIMIT @p0
        """

    Scenario: A source joined on its own key needs no tie-break of its own
      When I explain a page of the Orders joined with their Customers, by Order id
      Then the SQL ends "ORDER BY "t0"."id" LIMIT @p0"

    Scenario: A load by the key of the record it reads is not sorted
      When I explain the Orders binding the Customer each refers to
      Then the Customers load's SQL is:
        """
        SELECT "t0"."id", "t0"."name", "t0"."email" FROM "customers" AS "t0" WHERE "t0"."id" = ANY(@p0)
        """

    Scenario: A version's increment is written, not sent
      Given a Customer "Ada" with a published Order of 10
      When I change the Order to cancelled, then delete it
      Then the update sets "version" = "orders"."version" + 1
