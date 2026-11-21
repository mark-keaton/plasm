Feature: Text is matched as it is written, on every database

  StartsWith, EndsWith and Contains in a query match the text with its case
  and with every character as itself: a % or _ (PostgreSQL's LIKE) or a *, ?
  or [ (SQLite's GLOB) in the text is that character, not a wildcard. The
  same query finds the same rows on every database; SQLite's LIKE, which
  ignores ASCII case, is not what Plasm writes there.

  Background:
    Given a configured relational database

  Scenario Outline: Text is matched with its case
    Given customers named "Ada Lovelace", "ada lovelace", "Élodie" and "élodie"
    When I read the customers whose name <how> "<text>"
    Then the customers read are named <names>

    Examples:
      | how         | text     | names            |
      | starts with | Ada      | "Ada Lovelace"   |
      | starts with | ada      | "ada lovelace"   |
      | ends with   | Lovelace | "Ada Lovelace"   |
      | ends with   | LOVELACE | nobody           |
      | contains    | a Love   | "Ada Lovelace"   |
      | starts with | É        | "Élodie"         |
      | starts with | é        | "élodie"         |
      | contains    | LODIE    | nobody           |

  Scenario Outline: A wildcard in the text is that character
    Given customers named "50% off", "500 off", "a_b", "axb", "a*b", "a?b" and "[x]"
    When I read the customers whose name contains "<text>"
    Then the customers read are named <names>

    Examples:
      | text | names     |
      | %    | "50% off" |
      | _    | "a_b"     |
      | *    | "a*b"     |
      | ?    | "a?b"     |
      | [    | "[x]"     |

  Scenario: Text from a prepared query's argument is matched with its case
    Given customers named "Ada Lovelace", "ada lovelace", "Élodie" and "élodie"
    When I read the customers whose name starts with "ada", through a prepared query
    Then the customers read are named "ada lovelace"

  Scenario: An empty text matches every name
    Given customers named "Ada Lovelace" and "élodie"
    When I read the customers whose name ends with ""
    Then the customers read are named "Ada Lovelace" and "élodie"
