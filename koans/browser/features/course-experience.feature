# Behaviour specification: the learner's experience of the browser course.
Feature: Walking the path in the browser
  As a learner
  I want the browser course to keep my place and help me focus
  So that I can learn over several visits

  Background:
    Given the koans page has finished loading

  Rule: The course is the same course

    Scenario: The browser course has every lesson and koan of the terminal version
      When the browser course is compared with the koans' lesson specifications
      Then it has every lesson, in order, and every koan, in order, with the same guidance

    Scenario: With the reference answers every koan passes in the browser
      When every koan is filled in with its reference answer and run in the page
      Then every koan passes

    Scenario: As shipped no koan passes before it is answered
      When every koan is run in the page as shipped
      Then every koan asks for its blank

  Rule: Progress is kept

    Scenario: Answers and progress survive a reload
      Given the learner has passed the first two koans and edited the third
      When the learner reloads the page
      Then the first two koans are shown as passed
      And the third koan shows the learner's edit

    Scenario: Saved code stays with its koan when the course is rearranged
      Given this browser holds the code and progress of an older course, saved for the second koan of lesson 1
      When the learner reloads the page
      Then the koan "Values do not change" shows its original code
      And the koan "Values do not change" is not shown as passed

    Scenario: An edit to a koan that has since changed is set aside
      Given this browser holds an edit to the koan "Values do not change" made when its code read differently
      When the learner reloads the page
      Then the koan "Values do not change" shows its original code

    Scenario: A koan can be reset to its original code
      Given the learner has edited a koan
      When the learner resets it
      Then it shows its original code with its blanks

    Scenario: Progress is shown per lesson
      Given the learner has passed some koans
      Then every lesson shows how many of its koans are passed

    Scenario: Progress through the whole course is shown
      Given the learner has passed some koans
      Then the page shows how many of all the koans are passed

    Scenario: Only the lesson being studied is unfolded
      When a koan in a later lesson is shown
      Then that lesson's koans are listed
      And the other lessons show only their titles and progress

  Rule: The editor helps

    Scenario: Blanks are easy to find
      When a koan is shown
      Then each blank in its code is highlighted

    Scenario: The course links back to Plasm's specification
      When the learner opens the koan "Wrapped keys keep their meaning"
      Then it links to the acceptance scenario it teaches

    Scenario: The first lesson names the F# it teaches
      When the learner opens the first koan
      Then it names the F# topic it teaches, without a link

    Scenario: Code is coloured as F#
      When the learner opens the koan "A record needs nothing from Plasm"
      Then its keywords, type names, strings, numbers and comments are coloured

    Scenario: Colouring follows the learner's edits
      When the learner types code into a koan
      Then the code they typed is coloured too

    Scenario: Lines are numbered as compile errors number them
      When a koan is shown
      Then each line of its code is numbered, starting from 1

    Scenario: A koan can be run from the keyboard
      When the learner fills in the first koan's blanks and presses Ctrl+Enter
      Then the koan shows that it passed

    Scenario: The next koan can be opened from the keyboard
      When the learner fills in the first koan's blanks and presses Ctrl+Enter
      And the learner presses Ctrl+] in the editor
      Then the koan "Values do not change" is shown
      And the editor has the focus, with its first blank selected

    Scenario: The previous koan can be opened from the keyboard
      Given the learner opens the koan "Values do not change"
      When the learner presses Ctrl+[ in the editor
      Then the koan "Fill in the blank" is shown

    Scenario: The last koan of a lesson leads on to the next lesson
      Given the learner opens the last koan of lesson 1
      When the learner presses Ctrl+] in the editor
      Then the first koan of lesson 2 is shown

    Scenario: The Next koan button opens the koan ready to type
      When the learner fills in the first koan's blanks and presses Ctrl+Enter
      And the learner chooses the Next koan button
      Then the koan "Values do not change" is shown
      And the editor has the focus, with its first blank selected

    Scenario: A koan chosen from the list opens ready to type
      When the learner chooses the koan "A record groups named fields" from the list
      Then the koan "A record groups named fields" is shown
      And the editor has the focus, with its first blank selected

    Scenario: The page opens ready to type
      When the learner reloads the page
      Then the editor has the focus, with its first blank selected

    Scenario: The Next koan button names its key
      When the learner fills in the first koan's blanks and presses Ctrl+Enter
      Then the Next koan button shows the key that opens the next koan

    Scenario: F# operators are drawn as single symbols
      When a koan is shown
      Then its code is set in a font that joins operators such as |> and ->

  # A koan's blanks must be answerable from the page: its code, the data it
  # starts from and the definitions it uses. Each koan opens with them.
  Rule: The editor has a simple Vim mode
    Off until the learner turns it on. In the examples, ‸ marks the cursor, ⏎ a
    new line and · a space at the edge of a line; keys are typed as written, with
    <Esc>, <C-[> and <C-r> for Escape, Ctrl+[ and Ctrl+R.

    Scenario: Vim mode is off until the learner turns it on
      When a koan is shown
      Then Vim mode is off
      And typing in the editor inserts text

    Scenario: With Vim mode on, a koan opens in Normal mode on its first blank
      Given the learner has turned Vim mode on
      When the learner chooses the koan "Values do not change" from the list
      Then the editor is in Normal mode
      And the cursor is on the first blank

    Scenario: A koan can be answered without leaving the keyboard
      Given the learner has turned Vim mode on
      When the learner chooses the koan "Fill in the blank" from the list
      And types "cw"Widget"<Esc>" in the editor
      And presses Ctrl+Enter
      Then the koan shows that it passed

    Scenario: Vim mode is remembered
      Given the learner has turned Vim mode on
      When the learner reloads the page
      Then the editor is in Normal mode

    Scenario Outline: In Normal mode <keys> <does>
      Given the learner has turned Vim mode on
      And the editor holds "<before>"
      When the learner types "<keys>" in the editor
      Then the editor holds "<after>"
      And the editor is in <mode> mode

      Examples:
        | keys          | does                                      | before                | after                  | mode   |
        | l             | moves right                               | ‸abc                  | a‸bc                   | Normal |
        | h             | moves left                                | ab‸c                  | a‸bc                   | Normal |
        | j             | moves down, keeping the column            | a‸bc⏎def              | abc⏎d‸ef               | Normal |
        | k             | moves up, keeping the column              | abc⏎de‸f              | ab‸c⏎def               | Normal |
        | w             | moves to the next word                    | ‸let x = 1            | let ‸x = 1             | Normal |
        | b             | moves back a word                         | let x ‸= 1            | let ‸x = 1             | Normal |
        | e             | moves to the end of the word              | ‸shouldBe x           | shouldB‸e x            | Normal |
        | 0             | moves to the start of the line            | let ‸x                | ‸let x                 | Normal |
        | ^             | moves to the first character of the line  | ····let ‸x            | ····‸let x             | Normal |
        | $             | moves to the end of the line              | ‸let x⏎y              | let ‸x⏎y               | Normal |
        | G             | moves to the last line                    | ‸a⏎b⏎··c              | a⏎b⏎··‸c               | Normal |
        | gg            | moves to the first line                   | ··a⏎b⏎‸c              | ··‸a⏎b⏎c               | Normal |
        | 2w            | counts                                    | ‸a b c                | a b ‸c                 | Normal |
        | x             | deletes a character                       | a‸bc                  | a‸c                    | Normal |
        | dw            | deletes a word                            | ‸let x = 1            | ‸x = 1                 | Normal |
        | dd            | deletes a line                            | a⏎‸b⏎c                | a⏎‸c                   | Normal |
        | D             | deletes to the end of the line            | let ‸x = 1            | let‸·                  | Normal |
        | cw            | changes a word                            | shouldBe ‸__          | shouldBe ‸             | Insert |
        | cw42<Esc>     | fills in a blank                          | shouldBe ‸__          | shouldBe 4‸2           | Normal |
        | cc            | changes a line                            | a⏎··‸bc⏎d             | a⏎··‸⏎d                | Insert |
        | C             | changes to the end of the line            | let ‸x = 1            | let ‸                  | Insert |
        | rz            | replaces a character                      | a‸bc                  | a‸zc                   | Normal |
        | i             | inserts before the cursor                 | a‸bc                  | a‸bc                   | Insert |
        | a             | appends after the cursor                  | a‸bc                  | ab‸c                   | Insert |
        | I             | inserts at the start of the line          | ··a‸bc                | ··‸abc                 | Insert |
        | A             | appends at the end of the line            | a‸bc⏎d                | abc‸⏎d                 | Insert |
        | o             | opens a line below, keeping the indent    | ··a‸b⏎c               | ··ab⏎··‸⏎c             | Insert |
        | O             | opens a line above, keeping the indent    | a⏎··b‸c               | a⏎··‸⏎··bc             | Insert |
        | ix<Esc>       | leaves Insert mode with Escape            | a‸b                   | a‸xb                   | Normal |
        | ix<C-[>       | leaves Insert mode with Ctrl+[            | a‸b                   | a‸xb                   | Normal |
        | yyp           | copies a line below                       | ‸a⏎b                  | a⏎‸a⏎b                 | Normal |
        | ddP           | moves a line up                           | a⏎‸b                  | ‸b⏎a                   | Normal |
        | xu            | undoes                                    | a‸bc                  | a‸bc                   | Normal |
        | xu<C-r>       | redoes                                    | a‸bc                  | a‸c                    | Normal |

  Rule: The editor knows F#

    Scenario: The compiler lists what can follow repo.
      When the learner asks what can follow "repo." in the koan "Every koan is given a repo"
      Then the completions include All, One, Count, Insert and Explain
      And each completion says what it is

    Scenario: Completions come quickly once the compiler has warmed up
      Given the learner has asked for completions once
      When the learner asks what can follow "repo." in the koan "ok opens a Result"
      Then the completions arrive within 1500 milliseconds

  Rule: Each koan has its own address

    Scenario: Opening a koan puts it in the address
      When the learner chooses the koan "A record groups named fields" from the list
      Then the address names the koan "a-record-groups-named-fields"

    Scenario: An address opens its koan, ready to type
      When the learner goes to the address of the koan "values-do-not-change"
      Then the koan "Values do not change" is shown
      And the editor has the focus, with its first blank selected

    Scenario: A reload stays on the same koan
      Given the learner opens the koan "An option may be absent"
      When the learner reloads the page
      Then the koan "An option may be absent" is shown

    Scenario: Back returns to the koan before
      Given the learner chooses the koan "Values do not change" from the list
      And the learner presses Ctrl+] in the editor
      When the learner goes back in the browser
      Then the koan "Values do not change" is shown

    Scenario: An address naming no koan opens the first koan
      When the learner goes to the address of the koan "no-such-koan"
      Then the koan "Fill in the blank" is shown
      And the address names the koan "fill-in-the-blank"

    Scenario: Every koan has an address of its own
      When the address of every koan is worked out from its name
      Then no two koans share an address

  Rule: Each koan opens with what it is given

    Scenario: A koan shows the tables it uses, with their starting rows
      When the learner opens the koan "Wrapped keys keep their meaning"
      Then under Given the customers table shows its 3 starting rows
      And Ada's row shows the id 1

    Scenario: A NULL is shown as NULL
      When the learner opens the koan "Absent values are options"
      Then under Given the orders table shows NULL as the customer of order 4

    Scenario: A koan shows the definitions it uses, from the shop's source
      When the learner opens the koan "Stored values can be rejected"
      Then under Given the definition of Quantity includes "if v > 0 then Ok(Quantity v)"
      And the definition says it comes from Shop.fs

    Scenario: The fields a koan reads are marked in their record
      When the learner opens the koan "Absent values are options"
      Then under Given the definition of Order marks the fields Id, CustomerId and CancellationReason

    Scenario: A table changed with SQL is given too
      When the learner opens the koan "Stored values can be rejected"
      Then under Given the order_lines table shows its 4 starting rows

    Scenario: Every table is a click away
      When the learner opens the koan "Wrapped keys keep their meaning"
      And asks for all tables
      Then under Given every table of the koans database is shown

    Scenario: A koan shows only its own code
      When the learner opens the koan "The shop has three customers"
      Then its code starts with its first comment, not with fun repo -> or task {
      And it uses the repo it is given as repo

    Scenario: Definitions say whether they are your app's code or test helpers
      When the learner opens the koan "The shop has three customers"
      Then under Given the definition of Customer is labelled "your app"
      And under Given the definition of ok is labelled "test helper"
      And under Given the definition of ok includes "let ok (r: Result<'T, 'E>) : 'T ="

    Scenario: A koan that needs nothing has no Given
      When the learner opens the first koan
      Then no Given is shown
