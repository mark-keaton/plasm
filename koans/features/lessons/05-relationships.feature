# Lesson 05 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About relationships
  Relationships are loaded only when asked, in batched commands, and never by reading a field.

  Prerequisites: lesson 04.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                               | the learner discovers                                                     | teaches                                                            |
      | Not loaded is not empty            | Assoc distinguishes NotLoaded from Loaded []                              | core: Unloaded and empty relationships are distinguishable         |
      | Reading a field never queries      | a NotLoaded relationship stays NotLoaded however often it is read         | core: Reading a relationship never triggers hidden database access |
      | Loads are batched across parents   | preloading Lines for many Orders sends one command, not one per Order     | core: Relationship loading batches across parents                  |
      | Siblings load separately           | Lines and Payments load in their own commands, with no cartesian product  | core: Sibling collections load without a cartesian product         |
      | Loads nest                         | a child query can preload its own relationships                           | core: Nested loads compose                                         |
      | A child filter filters children    | filtering the loaded Payments does not remove any Order                   | core: A child filter does not implicitly filter parents            |
      | Filtering parents through children | Rel.any keeps each matching Order once                                    | elegance: Relationship filtering does not duplicate parents        |
      | At most n per parent               | limitPerParent keeps the latest Payments of each Order, not of all Orders | core: Per-parent limits preserve their stated meaning              |
      | Loading onto values you hold       | repo.Preload returns new values and leaves the originals unloaded         | core: Loading onto existing values preserves the originals         |
