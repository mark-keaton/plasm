# Executable acceptance specification (Reqnroll): a field unique within
# others, such as a name unique per tenant. Written before the implementation.
@implemented
Feature: A field can be unique within other fields
  As an F# application developer whose records belong to tenants
  I want a field's values unique within its tenant, stated once on the field
  So that two tenants may use one name, a duplicate within one is the field's
  error, and the database holds the rule

  [<Unique(Within = [| nameof the<Tag>.Tenant |])>] on a field states one
  uniqueness over the fields it names and then the field itself: (tenant,
  name), so an index over it also serves a tenant's reads. The DDL prints it
  under PostgreSQL's default name, <table>_<columns>_key, the schema check
  reports it missing or held under another name, and a violation is the error
  of the field that carries the attribute. Two fields unique within the same
  fields are two constraints, named by their own columns. A field in Within,
  and the field carrying it, are each one column and required: PostgreSQL
  counts absent values as distinct, so with an optional field two rows that
  F# calls equal would both be kept. A field of several columns (a value, a
  union, a reference to a key of several parts) is refused in Within for now.

  # Tag = { Id: TagId; Tenant: string; Name: string (unique within Tenant); Slug: string (unique within Tenant) }
  Background:
    Given a configured relational database
    And tags unique within their tenant

  Rule: A duplicate within the fields is the field's error

    Scenario: Two tenants may use one name; one tenant may not use it twice
      Given the Tag "urgent" of the tenant "acme"
      When I insert the Tag "urgent" of the tenant "globex"
      Then it is stored
      When I insert the Tag "urgent" of the tenant "acme" again
      Then the insert fails with the Name field's uniqueness error, naming tenant_tags_tenant_name_key

  Rule: The tables state and check the rule

    Scenario: The DDL declares each uniqueness under its own name
      When I print the DDL for Tags unique within a tenant
      Then it declares "constraint tenant_tags_tenant_name_key unique (tenant, name)"
      And it declares "constraint tenant_tags_tenant_slug_key unique (tenant, slug)"

    Scenario: A table without the uniqueness is reported
      Given the tenant_tags table has lost its constraint tenant_tags_tenant_name_key
      When I check the tenant tags against the database
      Then the missing uniqueness of Tag.Name within tenant is reported

    @postgres
    Scenario: A uniqueness held under another name is reported, with the rename
      Given the tenant_tags table's constraint tenant_tags_tenant_name_key is renamed tenant_tags_per_tenant
      When I check the tenant tags against the database
      Then tenant_tags_per_tenant is reported as holding Tag.Name's uniqueness, with the rename to tenant_tags_tenant_name_key

  Rule: What a field cannot be unique within is refused when the model is built

    Scenario Outline: A field unique within <within> is refused
      When I build a model of a record whose field is unique within <within>
      Then the field is refused, saying "<message>"

      Examples:
        | within                        | message                                            |
        | an optional field             | is unique within Region, which is optional         |
        | a field, while it is optional | is optional, and unique within Tenant              |
        | a field that does not exist   | is unique within Owner, which is not a field of    |
        | a value of several columns    | is unique within Place, which is stored in several columns |
