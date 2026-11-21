# Executable acceptance specification (Reqnroll): times are stored in UTC, and a
# value a provider cannot send is a failure, never an exception.
@implemented
Feature: Only UTC times are stored, and a value that cannot be sent is a failure
  As an application developer whose times come from users and clocks in many zones
  I want a time Plasm cannot store as the instant it is refused, as every failure is, by value
  So that no time is shifted by a server's time zone, and no write or read throws past my Result

  A DateTime whose Kind is not Utc, or a DateTimeOffset whose offset is not
  zero, is refused before anything is sent, naming the field or the value:
  PostgreSQL's timestamp with time zone holds an instant, and Npgsql would
  otherwise send such a DateTime as a timestamp without one, which the server
  reads in its own time zone (measured: 08:00 Unspecified stored as 07:00Z on
  a server in Paris). A write refuses it as an invalid change of its field; a
  query, a stream and a prepared query's argument as a query that cannot be
  translated, naming the value. And what
  a provider throws while sending a value it cannot send is a database
  failure, as any other the provider reports.

  # Appointment = { Id; Title: string; When: DateTime; At: DateTimeOffset; Window: Period }
  # Period = { From: DateTime; Until: DateTime }, a value record: two columns
  Background:
    Given a configured relational database
    And a table of appointments

  Scenario Outline: A write of a time that is not UTC is refused, naming the field
    When I insert an appointment whose <field> is <value>
    Then the write is refused as an invalid change of Appointment.<field>, saying "only UTC"
    And no appointment is stored

    Examples:
      | field | value                                  |
      | When  | 08:00 with Kind Unspecified            |
      | When  | 08:00 with Kind Local                  |
      | At    | 09:00 at the offset +01:00             |

  Scenario: A time that is UTC is stored as the instant it is
    When I insert an appointment at 08:00 UTC, as a DateTime and as a DateTimeOffset
    Then it is read back with When 08:00 with Kind Utc and At 08:00 at the offset +00:00

  Scenario: A change to a time that is not UTC is refused inside a transaction of several steps
    Given an appointment at 08:00 UTC
    When a transaction inserts a second and then moves the first to 09:00 with Kind Unspecified
    Then the transaction is refused as an invalid change of Appointment.When, saying "only UTC"
    And there is one appointment, at 08:00 UTC

  Scenario: A bulk write of a time that is not UTC is refused
    Given an appointment at 08:00 UTC
    When I move every appointment to 09:00 with Kind Local in one bulk update
    Then the transaction fails, saying "only UTC"
    And there is one appointment, at 08:00 UTC

  Scenario Outline: A query by a time that is not UTC is refused, naming the value
    Given an appointment at 08:00 UTC
    When I read the appointments <how> by 08:00 with Kind Unspecified
    Then the read is refused as a query that cannot be translated, naming the value and saying "only UTC"

    Examples:
      | how                       |
      | in a query                |
      | in a stream               |
      | through a prepared query  |
      | among a prepared list of times |
      | by their window           |

  Scenario: A value the provider cannot send is a database failure, not an exception
    When a transaction's own command sends a Uri, which no provider can send, as a parameter
    Then the transaction fails as a database failure, not an exception

  Scenario: A value of a type Plasm cannot send in hand-written SQL is a failure, not an exception
    When I read appointments with hand-written SQL comparing the title with a Uri
    Then the read is refused as a query naming the type Uri, which Plasm does not map

  # A time is kept to the microsecond, as PostgreSQL's timestamptz keeps it,
  # on every database: what a write keeps is what a query by the same value
  # finds. Measured on PostgreSQL 18 with Npgsql, which divides the ticks
  # since 2000-01-01 by ten: towards zero, so a time before 2000 loses its
  # tenth of a microsecond upwards.
  Scenario Outline: A time is kept to the microsecond and found by the value written
    Given a table of appointments
    When I insert an appointment whose When and At are <written> UTC
    Then it is read back with When and At <kept> UTC
    And it is found by When and by At <written> UTC

    Examples:
      | written                     | kept                       |
      | 2026-11-21 08:00:00.1234567 | 2026-11-21 08:00:00.123456 |
      | 2000-01-01 00:00:00.0000009 | 2000-01-01 00:00:00        |
      | 1999-12-31 23:59:59.9999995 | 2000-01-01 00:00:00        |
      | 1969-07-20 20:17:40.1234567 | 1969-07-20 20:17:40.123457 |

  # However a time is written (Plasm's writes, a transaction's own command,
  # the specs' own SQL), a tenth of a microsecond is not kept: CI's Linux
  # clock gives one, where the Mac's gives whole microseconds.
  Scenario Outline: A time is kept to the microsecond however it is written
    Given a table of appointments
    When an appointment whose When and At are 2026-11-21 08:00:00.1234567 UTC is written by <path>
    Then it is read back with When and At 2026-11-21 08:00:00.123456 UTC
    And it is found by When and by At 2026-11-21 08:00:00.1234567 UTC, in a query and through a prepared query

    Examples:
      | path                        |
      | an insert                   |
      | an update                   |
      | a bulk insert               |
      | a bulk update               |
      | a transaction's own command |
      | the scenario's own SQL      |

  Scenario: Times are ordered as the instants they are, whatever their fractions
    Given a table of appointments
    And appointments at 08:00:00.5, 08:00:00, 08:00:00.25, 08:00:00.000001 and 07:59:59.999999 UTC
    When I read the appointments ordered by When and by At
    Then both are in the order 07:59:59.999999, 08:00:00, 08:00:00.000001, 08:00:00.25, 08:00:00.5

  @sqlite
  Scenario: A time column holds one form, held to it by its check, on SQLite
    Given an appointment at 08:00 UTC
    When the appointment's when column is set to "2026-11-21T08:00:00Z", as another tool might write it
    Then the database refuses it, by the column's check

  @sqlite
  Scenario Outline: A time column holding another form is a read error naming the column, on SQLite
    Given an appointment at 08:00 UTC
    And the appointment's <column> column is set past its check to "<text>"
    When I read the appointments
    Then the read of the appointments fails, saying "<column>" and "not a time in Plasm's form"
    And the read's error says a command sent past Plasm must write a time to the microsecond in UTC

    Examples:
      | column | text                          |
      | when   | 2026-11-21T08:00:00Z          |
      | when   | 2026-11-21 08:00:00.1234567   |
      | at     | 2026-11-21 09:00:00+01:00     |
      | at     | 2026-11-21 08:00:00           |
