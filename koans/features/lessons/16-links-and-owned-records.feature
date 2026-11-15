# Lesson 16 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About many-to-many and has-one
  Join-table relationships and one-to-one relationships load and filter like any other; link writes touch only the join table.

  Prerequisites: lesson 09, lesson 12.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                              | the learner discovers                                             | teaches                                                                 |
      | Tags wait to be asked for         | without preload, an Article's Tags are NotLoaded                  | core: Unloaded and empty relationships are distinguishable              |
      | Reading an article's tags         | preload a.Tags fills Tags with the Article's linked Tags          | many-to-many: Preloading a many-to-many relationship                    |
      | An account's profile is an option | a has-one relationship loads as Some profile                      | has-one: Preloading a has-one relationship                              |
      | An account without a profile      | an Account with no Profile has its Profile loaded as None         | has-one: Preloading a has-one relationship                              |
      | Tags load through the join table  | preloading Tags is one batched command; a shared Tag is one value | many-to-many: Preloading a many-to-many relationship                    |
      | No tags is an empty, loaded list  | an Article without links has Loaded []                            | many-to-many: An Article without Tags has an empty, loaded relationship |
      | Filtering through links           | Rel.any through a join table keeps each Article once              | many-to-many: Filtering through a many-to-many relationship             |
      | Replacing links                   | putLinks makes the join table match a set, and writes no Tag      | many-to-many: Replacing an Article's Tags                               |
      | Adding and removing one link      | addLinks and removeLinks change only the links named              | many-to-many: Adding and removing individual links                      |
      | At most one related record        | a has-one relationship loads as Some or None                      | has-one: Preloading a has-one relationship                              |
      | Filtering through has-one         | Rel.exists filters through an optional relationship               | has-one: Filtering through a has-one relationship                       |
