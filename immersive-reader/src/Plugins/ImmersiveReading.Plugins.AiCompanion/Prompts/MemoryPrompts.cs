namespace ImmersiveReading.Plugins.AiCompanion.Prompts;

/// <summary>
/// Built-in templates of the characters' memory (specs 19 and 24). They are registered with the host, so the user can edit them in
/// Settings -> Prompts; an edited copy must keep the variables declared here.
/// </summary>
public static class MemoryPrompts
{
    public const string BlockName = "persona.memory";
    public const string ExtractName = "memory.extract";

    public const string Block = """
        ---
        name: persona.memory
        version: 1
        purpose: The block added to a character's system prompt - what the user told the characters earlier, and story facts that fit the user's message.
        variables:
          - user_facts | type=list | untrusted
          - story_facts | type=list | untrusted
        ---
        {{#if user_facts}}WHAT THE USER TOLD YOU EARLIER (they said this themselves in past conversations; remember it the way a friend would. Bring it up only when it fits, never recite it, never invent more about them):
        {{#each user_facts}}- {{this}}
        {{/each}}{{/if}}{{#if story_facts}}STORY FACTS THAT FIT THE USER'S MESSAGE (what the reader already knows, nothing later):
        {{#each story_facts}}- {{this}}
        {{/each}}{{/if}}
        """;

    public const string Extract = """
        ---
        name: memory.extract
        version: 1
        purpose: Finds the facts a user states about themselves in one chat message, so the characters can remember them.
        response: json
        variables:
          - user_message | required | untrusted
          - known_facts | type=list | untrusted
          - max_facts | type=int | default=3
        ---
        You read ONE message that a person wrote to a character in a chat. Find the facts the person states about THEMSELVES or about their own life that a friend would remember later: name, family, pets, job or school, where they live, tastes, plans, worries, things they own or do regularly.

        RULES
        1. Only what the person says in this message. Never use the character's words, a book, or your own knowledge. If the message states nothing about the person, return an empty list.
        2. Not facts: questions, greetings, opinions about the book, passing moods that will not matter tomorrow, requests to the character.
        3. Write each fact as one short sentence in the same language as the message, about "the user" in the third person (Japanese: ユーザー).
        4. "quote" is an exact piece of the message, copied character for character, that states the fact. A fact without a verbatim quote is discarded.
        5. Skip a fact that says the same as one in KNOWN_FACTS. At most {{max_facts}} facts. When unsure, leave it out.

        MESSAGE:
        {{user_message}}

        KNOWN_FACTS:
        {{#each known_facts}}- {{this}}
        {{/each}}
        Return only JSON: {"facts":[{"text":"","subject":"","quote":""}]}
        """;

    public static readonly string[] All = [Block, Extract];
}
