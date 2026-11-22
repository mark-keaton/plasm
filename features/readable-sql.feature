# Executable acceptance specification (Reqnroll): the SQL Plasm sends reads as
# a person would write it. Written before the implementation, from the
# comparison of Plasm's SQL with EF Core's (2026-11-17).
@implemented
Feature: The SQL a query sends reads as a person would write it
  As an F# application developer reading a log
  I want the SQL Plasm sends to say what it does and no more
  So that I can read a slow query's text and know which F# query it is

  Each clause of a statement starts a line; a derived table is indented under
  its parenthesis, which closes on a line of its own with the table's name; an
  EXISTS stays on the line of the condition it is part of. A source is named
  after the F# variable that reads it, quoted like every name; one with no
  variable (a load by a tie, a tuple or `_` pattern) is its table under its
  own name; a variable that a source of the same statement already names, or
  that Plasm names a table it derives ("ranked", "d", "q"), is numbered (o, o2).
  A column is read under its own name, with no alias repeating it; a record
  selected names its columns after its fields. Conditions joined by AND or OR
  are written as one list, with parentheses only where precedence needs them
  (an OR inside an AND, an operation inside another). An ascending order is not
  marked; a negated equality of required values is `<>`; an if, elif and else is one
  CASE;
  a derived table has its own name, never the name of the table inside it. A
  statement names its table once, and a NULL written is written, not sent.
  An ordering by an option keeps F#'s None first only where a row may hold
  None: not where the query's conditions prove the column present (testing it,
  comparing it, or testing the union case whose required field it holds), so an
  ordinary index can serve the order. A row whose union case and its columns
  disagree, which Plasm never writes and refuses to read, then sorts where the
  database puts NULLs: first in a descending order, so it is refused on the
  first page. None of this changes what a query reads or writes.

  # Customer = { Id: CustomerId; Name: string; [<Unique>] Email: string }
  # Order = { Id: OrderId; CustomerId: CustomerId option; Status: OrderStatus; Total: decimal; CancellationReason: string option; ... }
  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: A read says what it reads and no more

    @postgres
    Scenario: Each clause on a line, columns under their own names, conditions in one list, ascending unmarked
      When I explain the Customers named "Ada" whose email starts with "a", other than 3, by name and then email descending, taking 10
      Then the SQL is:
        """
        SELECT "c"."id", "c"."name", "c"."email"
        FROM "customers" AS "c"
        WHERE "c"."name" = @p0 AND "c"."email" LIKE @p1 AND "c"."id" <> @p2
        ORDER BY "c"."name", "c"."email" DESC
        LIMIT @p3
        """

    @sqlite
    Scenario: Each clause on a line, columns under their own names, conditions in one list, ascending unmarked on SQLite
      When I explain the Customers named "Ada" whose email starts with "a", other than 3, by name and then email descending, taking 10
      Then the SQL is:
        """
        SELECT "c"."id", "c"."name", "c"."email"
        FROM "customers" AS "c"
        WHERE "c"."name" = @p0 AND "c"."email" GLOB @p1 AND "c"."id" <> @p2
        ORDER BY "c"."name", "c"."email" DESC
        LIMIT @p3
        """

    @postgres
    Scenario: An OR inside an AND keeps its parentheses, and a negated equality is <>
      When I explain the ids and totals of the Orders over 10 or under 2 that are not drafts
      Then the SQL is:
        """
        SELECT "o"."id" AS "c0", "o"."total" AS "c1"
        FROM "orders" AS "o"
        WHERE ("o"."total" > @p0 OR "o"."total" < @p1) AND "o"."status" <> @p2
        """

    @sqlite
    Scenario: An OR inside an AND keeps its parentheses, and a negated equality is <> on SQLite
      When I explain the ids and totals of the Orders over 10 or under 2 that are not drafts
      Then the SQL is:
        """
        SELECT "o"."id" AS "c0", "o"."total" AS "c1"
        FROM "orders" AS "o"
        WHERE (plasm_decimal_compare("o"."total", @p0) > 0 OR plasm_decimal_compare("o"."total", @p1) < 0) AND "o"."status" <> @p2
        """

    Scenario: A record selected names its columns after its fields
      When I explain the Orders joined with their Customers, selecting a record of the total and the name
      Then the SQL is:
        """
        SELECT "c"."name" AS "Name", "o"."total" AS "Total"
        FROM "orders" AS "o"
        INNER JOIN "customers" AS "c" ON "o"."customer_id" = "c"."id"
        """

    @postgres
    Scenario: An operation inside a comparison needs no parentheses
      When I explain the Orders without a cancellation reason whose doubled total is over 10
      Then the SQL is:
        """
        SELECT "o"."id", "o"."customer_id", "o"."status", "o"."total", "o"."cancellation_reason", "o"."placed_at", "o"."version"
        FROM "orders" AS "o"
        WHERE "o"."cancellation_reason" IS NULL AND "o"."total" * @p0 > @p1
        """

    @sqlite
    Scenario: An operation inside a comparison needs no parentheses on SQLite
      When I explain the Orders without a cancellation reason whose doubled total is over 10
      Then the SQL is:
        """
        SELECT "o"."id", "o"."customer_id", "o"."status", "o"."total", "o"."cancellation_reason", "o"."placed_at", "o"."version"
        FROM "orders" AS "o"
        WHERE "o"."cancellation_reason" IS NULL AND plasm_decimal_compare(plasm_decimal_multiply("o"."total", @p0), @p1) > 0
        """

    @postgres
    Scenario: A derived table has a name of its own, and is indented under its parenthesis
      When I explain the Orders by total, binding their two newest Payments
      Then the Payments load's SQL is:
        """
        SELECT "ranked"."id", "ranked"."order_id", "ranked"."amount", "ranked"."status", "ranked"."created_at"
        FROM (
            SELECT "p"."id", "p"."order_id", "p"."amount", "p"."status", "p"."created_at", ROW_NUMBER() OVER (PARTITION BY "p"."order_id" ORDER BY "p"."created_at" DESC, "p"."id") AS "__rn"
            FROM "payments" AS "p"
            WHERE "p"."order_id" = ANY(@p0)
        ) AS "ranked"
        WHERE "ranked"."__rn" <= @p1
        ORDER BY "ranked"."__rn"
        """

    @sqlite
    Scenario: A derived table has a name of its own, and is indented under its parenthesis on SQLite
      When I explain the Orders by total, binding their two newest Payments
      Then the Payments load's SQL is:
        """
        SELECT "ranked"."id", "ranked"."order_id", "ranked"."amount", "ranked"."status", "ranked"."created_at"
        FROM (
            SELECT "p"."id", "p"."order_id", "p"."amount", "p"."status", "p"."created_at", ROW_NUMBER() OVER (PARTITION BY "p"."order_id" ORDER BY "p"."created_at" DESC, "p"."id") AS "__rn"
            FROM "payments" AS "p"
            WHERE "p"."order_id" IN (@p0)
        ) AS "ranked"
        WHERE "ranked"."__rn" <= @p1
        ORDER BY "ranked"."__rn"
        """

    @postgres
    Scenario: An if, elif and else is one CASE
      When I explain the Orders' sizes in words, through if, elif and else on the total
      Then the SQL is:
        """
        SELECT CASE WHEN "o"."total" > @p0 THEN 'big' WHEN "o"."total" > @p1 THEN 'medium' ELSE 'small' END AS "c0"
        FROM "orders" AS "o"
        """

    @sqlite
    Scenario: An if, elif and else is one CASE on SQLite
      When I explain the Orders' sizes in words, through if, elif and else on the total
      Then the SQL is:
        """
        SELECT CASE WHEN plasm_decimal_compare("o"."total", @p0) > 0 THEN 'big' WHEN plasm_decimal_compare("o"."total", @p1) > 0 THEN 'medium' ELSE 'small' END AS "c0"
        FROM "orders" AS "o"
        """

    @postgres
    Scenario: Every line meeting a condition is no line failing it, written plainly on the condition's line
      When I explain the Orders all of whose lines cost more than 5
      Then the SQL ends "WHERE NOT EXISTS (SELECT 1 FROM "order_lines" AS "l" WHERE "l"."order_id" = "o"."id" AND "l"."unit_price" <= @p0)"

    @sqlite
    Scenario: Every line meeting a condition is no line failing it, written plainly on the condition's line on SQLite
      When I explain the Orders all of whose lines cost more than 5
      Then the SQL ends "WHERE NOT EXISTS (SELECT 1 FROM "order_lines" AS "l" WHERE "l"."order_id" = "o"."id" AND plasm_decimal_compare("l"."unit_price", @p0) <= 0)"

    Scenario: An option the query has proved present is ordered as a plain column
      When I explain the Orders with a cancellation reason, ordered by it
      Then the SQL ends "ORDER BY "o"."cancellation_reason""
      And the SQL has the line "WHERE "o"."cancellation_reason" IS NOT NULL"

    Scenario: An option the query has not proved present keeps None first
      When I explain the Orders ordered by their cancellation reason
      Then the SQL ends "ORDER BY "o"."cancellation_reason" NULLS FIRST"

  Rule: A source is named after the variable that reads it

    @postgres
    Scenario: A subquery's variable that shadows the query's is numbered
      When I explain the Orders for which some Order is over 100, both read by o
      Then the SQL ends "WHERE EXISTS (SELECT 1 FROM "orders" AS "o2" WHERE "o2"."total" > @p0)"

    @sqlite
    Scenario: A subquery's variable that shadows the query's is numbered on SQLite
      When I explain the Orders for which some Order is over 100, both read by o
      Then the SQL ends "WHERE EXISTS (SELECT 1 FROM "orders" AS "o2" WHERE plasm_decimal_compare("o2"."total", @p0) > 0)"

    @postgres
    Scenario: A variable that is no identifier is quoted like any name
      When I explain the Orders over 10, read by ``an order``
      Then the SQL is:
        """
        SELECT "an order"."id", "an order"."customer_id", "an order"."status", "an order"."total", "an order"."cancellation_reason", "an order"."placed_at", "an order"."version"
        FROM "orders" AS "an order"
        WHERE "an order"."total" > @p0
        """

    @sqlite
    Scenario: A variable that is no identifier is quoted like any name on SQLite
      When I explain the Orders over 10, read by ``an order``
      Then the SQL is:
        """
        SELECT "an order"."id", "an order"."customer_id", "an order"."status", "an order"."total", "an order"."cancellation_reason", "an order"."placed_at", "an order"."version"
        FROM "orders" AS "an order"
        WHERE plasm_decimal_compare("an order"."total", @p0) > 0
        """

    Scenario: A load's variable never takes the name of the table Plasm derives
      When I explain the Orders by total, binding their two newest Payments read by ranked
      Then the Payments load's SQL has the line "    FROM "payments" AS "ranked2""

  Rule: A write names its table once, and starts a line for each clause

    Scenario: An insert's values and what it reads back each start a line
      When I insert a Customer "Ada"
      Then the insert is:
        """
        INSERT INTO "customers" ("name", "email")
        VALUES (@p0, @p1)
        RETURNING "id", "name", "email"
        """

    Scenario: An update and a delete name their table once
      Given a Customer "Ada" with a published Order of 10
      When I change the Order to cancelled, then delete it
      Then the update's first line is "UPDATE "orders"" and it has no alias
      And the delete is:
        """
        DELETE FROM "orders"
        WHERE "orders"."id" = @p0 AND "orders"."version" = @p1
        """

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
        SELECT "c"."id", "c"."name", "c"."email"
        FROM "customers" AS "c"
        WHERE "c"."name" = @p0
        LIMIT 2
        """

    Scenario: Asking whether any row exists reads one, written as 1
      Given a Customer "Ada" with a published Order of 10
      When I ask whether a Customer is named "Ada"
      Then the command is:
        """
        SELECT 1 AS "c0"
        FROM "customers" AS "c"
        WHERE "c"."name" = @p0
        LIMIT 1
        """

    Scenario: A unique column is a tie-break of its own
      When I explain a page of the Customers by email
      Then the SQL is:
        """
        SELECT "c"."id", "c"."name", "c"."email"
        FROM "customers" AS "c"
        ORDER BY "c"."email"
        LIMIT @p0
        """

    Scenario: A source joined on its own key needs no tie-break of its own
      When I explain a page of the Orders joined with their Customers, by Order id
      Then the SQL ends:
        """
        ORDER BY "o"."id"
        LIMIT @p0
        """

    @postgres
    Scenario: A load by the key of the record it reads is not sorted, and its table, read by no variable, is named once
      When I explain the Orders binding the Customer each refers to
      Then the Customers load's SQL is:
        """
        SELECT "customers"."id", "customers"."name", "customers"."email"
        FROM "customers"
        WHERE "customers"."id" = ANY(@p0)
        """

    @sqlite
    Scenario: A load by the key of the record it reads is not sorted, and its table, read by no variable, is named once on SQLite
      When I explain the Orders binding the Customer each refers to
      Then the Customers load's SQL is:
        """
        SELECT "customers"."id", "customers"."name", "customers"."email"
        FROM "customers"
        WHERE "customers"."id" IN (@p0)
        """

    Scenario: A version's increment is written, not sent
      Given a Customer "Ada" with a published Order of 10
      When I change the Order to cancelled, then delete it
      Then the update sets "version" = "orders"."version" + 1
