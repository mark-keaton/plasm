# Executable acceptance specification (Reqnroll): Plasm.CSharp, what a C#
# host is given. Written before the implementation.
@implemented
Feature: A C# host is given one of the cases an outcome can have
  As an F# developer whose data layer a C# host calls
  I want the outcomes C# switches over written once, in a package
  So that each project writes its views, not the cases every project retypes

  Plasm.CSharp holds the cases C# switches over: Missing, Refused, Stale,
  Invalid; the unions over them, each a struct of C#'s union shape: Found<'T>
  (the value or Missing), Added<'T> (the value or Invalid), Answered<'T> (the
  value, Missing or Invalid) and Changed<'T> (the value, Missing, Refused,
  Stale or Invalid); and the functions that read a read's or a transaction's
  result as one of them, and a read of many as an array of views. Each
  project maps its own reason for stopping a transaction to the case it
  means; a database that fails throws UnavailableException. It depends on
  Plasm alone, so a host of either provider takes it, and an F# host need
  not.

  # Note = { Id: NoteId; [<Unique>] Title: string; [<Version>] Version: int }
  # NoteView = { Id: Guid; Title: string; Version: int }
  # A note's transaction stops with NoteStop: Gone, or TitleTaken of string.
  Background:
    Given a configured relational database
    And notes, each with a unique title and a version

  Rule: A read that can miss is Found

    Scenario: A note that is there is found as its view
      Given the note "Groceries"
      When C# reads the note "Groceries"
      Then it is given the NoteView of "Groceries"

    Scenario: A note that is not there is Missing, saying what is missing
      When C# reads a note that is not there
      Then it is given Missing, saying "note"

  Rule: A read of many is an array of views

    Scenario: A read of many is each record's view, in the query's order
      Given the notes "Groceries" and "Errands"
      When C# reads every note, ordered by title
      Then it is given the NoteViews of "Errands" and "Groceries", in that order

    Scenario: A read of none is an empty array
      When C# reads every note, ordered by title
      Then it is given no NoteView

    Scenario: A read of many the database fails throws UnavailableException, carrying the provider's exception
      Given the notes table is gone
      When C# reads every note, ordered by title
      Then UnavailableException is thrown, naming the read of "notes", with the provider's exception inside

  Rule: A change is the record as stored, or why not

    Scenario: A change is the note as stored
      Given the note "Groceries"
      When C# renames "Groceries" to "Shopping"
      Then it is given the NoteView of "Shopping", at version 2

    Scenario: A transaction stopped by the rules is the case the project maps it to
      Given the note "Groceries"
      When C# renames "Groceries" to "Groceries", which the rules refuse
      Then it is given Refused, with the code "TitleTaken" and the rules' words

    Scenario: A record gone is the case the project maps it to
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

  Rule: An add is the record as stored, or the input that was not valid

    Scenario: An add is the note as stored
      When C# adds the note "Groceries"
      Then it is given the NoteView of "Groceries", at version 1

    Scenario: An add the database refuses is Invalid
      Given the note "Groceries"
      When C# adds the note "Groceries"
      Then it is given Invalid, with the field "title" and "is already taken"

    Scenario: Input refused before the database is Invalid, by its field
      When C# adds a note whose title the domain refuses
      Then it is given Invalid, with the field "title" and "is empty"

  Rule: A request answered is the value, Missing or Invalid

    Scenario: A request whose record is gone is answered Missing
      When C# renames a note that is not there, as a request answered
      Then it is given Missing, saying "note"

    Scenario: A request whose input the database refuses is answered Invalid
      Given the notes "Groceries" and "Errands"
      When C# renames "Errands" to "Groceries", as a request answered
      Then it is given Invalid, with the field "title" and "is already taken"

  Rule: A database that fails throws

    Scenario: A read the database fails throws UnavailableException, carrying the provider's exception
      Given the notes table is gone
      When C# reads the note "Groceries"
      Then UnavailableException is thrown, naming the read, with the provider's exception inside

    Scenario: A change the database fails throws UnavailableException
      Given the notes table is gone
      When C# renames "Groceries" to "Shopping"
      Then UnavailableException is thrown, naming the step that failed
