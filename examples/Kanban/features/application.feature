Feature: The application checks its database before it serves anything

  Plasm creates and alters no tables. The application applies its own SQL
  migrations as it starts, then asks Plasm whether the tables fit the model,
  and serves nothing if they do not.

  @own-database
  Scenario: The application serves its API over HTTP
    Given the application is running on its own database
    When a browser asks for the boards over HTTP
    Then the answer is an empty list

  @own-database
  Scenario: The application refuses to start on a database its model does not fit
    Given a database where the column "cards.stage_resolution_reason" is missing
    When the application starts
    Then it fails to start, naming "cards.stage_resolution_reason"

  @own-database
  Scenario: A browser stays signed in through a cookie
    Given the application is running on its own database
    And a browser that registered as "Margaret" with the password "moonshot-1969"
    When that browser creates the board "apollo" over HTTP
    Then the answer is the board "apollo"
    And the cookie cannot be read by scripts

  @own-database
  Scenario: A browser without the cookie cannot change anything
    Given the application is running on its own database
    When a browser that never signed in creates the board "apollo" over HTTP
    Then the answer is to sign in

  Scenario: No query the application declares is one Plasm cannot translate
    When every query the application declares is checked against its model
    Then none of them is refused
