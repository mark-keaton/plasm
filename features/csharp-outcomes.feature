# Executable acceptance specification (Reqnroll): Plasm.CSharp, what a C#
# host is given. Written before the implementation.
@implemented
Feature: A C# host is given one of the cases an outcome can have
  As an F# developer whose data layer a C# host calls
  I want the outcomes C# switches over written once, in a package
  So that no project retypes the cases, nor maps its records and refusals to them

  Plasm.CSharp holds the cases C# switches over: Missing, Refused, Stale,
  Invalid; the unions over them, each a struct of C#'s union shape: FindResult<'T>
  (the value or Missing), AddResult<'T> (the value or Invalid), RequestResult<'T> (the
  value, Missing or Invalid), ChangeResult<'T> (the value, Missing, Refused, Stale
  or Invalid) and ChangeResult<'T, 'R> (the value, Missing, the domain's refusal
  'R, Stale or Invalid); and the functions that read a read's or a
  transaction's result as one of them, the records as they are, and a read
  of many as an array. The seam gives C# their views; FindResult.map, AddResult.map,
  RequestResult.map and ChangeResult.map make a view in F#. A transaction stops in the
  domain's terms with Stop: Stop.Missing, naming what is not there, or
  Stop.Refused, with the domain's refusal. A database that fails throws
  UnavailableException. It depends on Plasm alone, so a host of either
  provider takes it, and an F# host need not.

  # Note = { Id: NoteId; [<Unique>] Title: string; [<Version>] Version: int }
  # NoteView = { Id: Guid; Title: string; Version: int }
  # A note's transaction stops with Stop<NoteRefusal>: Stop.Missing "note",
  # or Stop.Refused (TitleTaken title), whose Message is the rules' words.
  Background:
    Given a configured relational database
    And notes, each with a unique title and a version

  Rule: A read that can miss is Found

    Scenario: A note that is there is found as the record
      Given the note "Groceries"
      When C# reads the note "Groceries"
      Then it is given the Note "Groceries"

    Scenario: A note that is not there is Missing, saying what is missing
      When C# reads a note that is not there
      Then it is given Missing, saying "note"

  Rule: A read of many is an array

    Scenario: A read of many is each record, in the query's order
      Given the notes "Groceries" and "Errands"
      When C# reads every note, ordered by title
      Then it is given the Notes "Errands" and "Groceries", in that order

    Scenario: A read of none is an empty array
      When C# reads every note, ordered by title
      Then it is given no Note

    Scenario: A read of many the database fails throws UnavailableException, carrying the provider's exception
      Given the notes table is gone
      When C# reads every note, ordered by title
      Then UnavailableException is thrown, naming the read of "notes", with the provider's exception inside

  Rule: A change is the record as stored, or why not

    Scenario: A change is the note as stored
      Given the note "Groceries"
      When C# renames "Groceries" to "Shopping"
      Then it is given the Note "Shopping", at version 2

    Scenario: A transaction the rules stop with Stop.Refused is the domain's refusal
      Given the note "Groceries"
      When C# renames "Groceries" to "Groceries", which the rules refuse
      Then it is given the refusal TitleTaken "Groceries"

    Scenario: A transaction stopped with Stop.Missing is Missing, saying what the transaction named
      When C# renames a note that is not there
      Then it is given Missing, saying "note"

    Scenario: A stale copy is Stale, naming the record type and its key
      Given the note "Groceries"
      And someone else renamed "Groceries" to "Shopping"
      When C# renames "Groceries" to "Errands" from the copy it read first
      Then it is given Stale, naming Note and the note's id

    Scenario: A key of several parts is Stale with each part, a "/" kept inside its part
      Given the keyed Issue plasm/plasm#7 "Crash", changed since it was read
      When C# retitles plasm/plasm#7 from the copy it read first
      Then it is given Stale, naming Issue and the parts "plasm/plasm" and "7"

    Scenario: A change the database refuses is Invalid, each field as C# names it
      Given the notes "Groceries" and "Errands"
      When C# renames "Errands" to "Groceries"
      Then it is given Invalid, with the field "title" and "is already taken"

  Rule: A change is one call: the record as the caller saw it, the rule's decision, the write

    Scenario: repo.Change stores what the rule decides, and answers the record as stored
      Given the note "Groceries"
      When C# renames "Groceries" to "Shopping" with repo.Change
      Then it is given the Note "Shopping", at version 2

    Scenario: repo.Change of a record that is not there is Missing, saying what
      When C# renames a note that is not there with repo.Change
      Then it is given Missing, saying "note"

    Scenario: repo.Change from a copy older than the stored one is Stale
      Given the note "Groceries"
      And someone else renamed "Groceries" to "Shopping"
      When C# renames "Groceries" to "Errands" with repo.Change, from the copy it read first
      Then it is given Stale, naming Note and the note's id

    Scenario: repo.Change that the rule refuses is the domain's refusal, and nothing is written
      Given the note "Groceries"
      When C# renames "Groceries" to "Groceries" with repo.Change, which the rule refuses
      Then it is given the refusal TitleTaken "Groceries"
      And the note "Groceries" is still at version 1

  Rule: An add is the record as stored, or the input that was not valid

    Scenario: An add is the note as stored
      When C# adds the note "Groceries"
      Then it is given the Note "Groceries", at version 1

    Scenario: An add the database refuses is Invalid
      Given the note "Groceries"
      When C# adds the note "Groceries"
      Then it is given Invalid, with the field "title" and "is already taken"

    Scenario: Input refused before the database is Invalid, by its field
      When C# adds a note whose title the domain refuses
      Then it is given Invalid, with the field "title" and "is empty"

  Rule: A request answered is the value, Missing or Invalid

    Scenario: A request whose input the rules refuse with Stop.Refused is answered Invalid
      Given the note "Groceries"
      When C# renames "Groceries" to "Groceries", as a request answered
      Then it is given Invalid, with the field "title" and "is already Groceries"

    Scenario: A request whose record is gone is answered Missing
      When C# renames a note that is not there, as a request answered
      Then it is given Missing, saying "note"

    Scenario: A request whose input the database refuses is answered Invalid
      Given the notes "Groceries" and "Errands"
      When C# renames "Errands" to "Groceries", as a request answered
      Then it is given Invalid, with the field "title" and "is already taken"

  Rule: An outcome's record is made a view in F# with its map

    Scenario: FindResult.map makes the record its view
      Given the note "Groceries"
      When C# reads the note "Groceries", made a view with FindResult.map
      Then it is given the NoteView of "Groceries"

    Scenario: FindResult.map keeps Missing
      When C# reads a note that is not there, made a view with FindResult.map
      Then it is given Missing, saying "note"

    Scenario: ChangeResult.map makes the record its view, and keeps the refusal
      Given the note "Groceries"
      When C# renames "Groceries" to "Shopping", made a view with ChangeResult.map
      Then it is given the NoteView of "Shopping", at version 2

    Scenario: ChangeResult.refused makes the refusal Refused, as a host without the seam takes it
      Given the note "Groceries"
      When C# renames "Groceries" to "Groceries", its refusal made Refused with ChangeResult.refused
      Then it is given Refused, with the code "TitleTaken" and the rules' words

    Scenario: AddResult.map makes the stored record its view
      When C# adds the note "Groceries", made a view with AddResult.map
      Then it is given the NoteView of "Groceries", at version 1

  Rule: A database that fails throws

    Scenario: A read the database fails throws UnavailableException, carrying the provider's exception
      Given the notes table is gone
      When C# reads the note "Groceries"
      Then UnavailableException is thrown, naming the read, with the provider's exception inside

    Scenario: A change the database fails throws UnavailableException
      Given the notes table is gone
      When C# renames "Groceries" to "Shopping"
      Then UnavailableException is thrown, naming the step that failed
