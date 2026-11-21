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
