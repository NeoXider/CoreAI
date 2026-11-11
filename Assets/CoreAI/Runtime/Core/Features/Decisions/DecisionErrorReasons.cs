namespace CoreAI.Decisions
{
    /// <summary>
    /// Stable named reasons carried by <see cref="DecisionResult.ErrorReason"/>,
    /// <see cref="DecisionValidationError.Code"/> and routing refusals. Callers branch on these, never on text.
    /// </summary>
    public static class DecisionErrorReasons
    {
        /// <summary>The request object was null.</summary>
        public const string RequestMissing = "request_missing";

        /// <summary>The request has no questions.</summary>
        public const string NoQuestions = "no_questions";

        /// <summary>A question entry was null.</summary>
        public const string QuestionMissing = "question_missing";

        /// <summary>A question id is empty, too long or uses characters outside ASCII letters, digits, <c>_ . -</c>.</summary>
        public const string InvalidQuestionId = "invalid_question_id";

        /// <summary>Two questions share an id.</summary>
        public const string DuplicateQuestionId = "duplicate_question_id";

        /// <summary>A choice question has no options.</summary>
        public const string TooFewOptions = "too_few_options";

        /// <summary>A choice question has more than <see cref="DecisionLimits.MaxChoiceOptions"/> options.</summary>
        public const string TooManyOptions = "too_many_options";

        /// <summary>An option is null or has an empty id.</summary>
        public const string InvalidOptionId = "invalid_option_id";

        /// <summary>Two options of one question share an id.</summary>
        public const string DuplicateOptionId = "duplicate_option_id";

        /// <summary>A score question has fewer than 1 or more than 10 levels.</summary>
        public const string ScoreLevelsOutOfRange = "score_levels_out_of_range";

        /// <summary>An image is null, not an image, or not inline data.</summary>
        public const string UnsupportedImage = "unsupported_image";

        /// <summary>The backend's wire cannot carry this image format (for example WebP as a TypeSafe asset).</summary>
        public const string UnsupportedImageFormat = "unsupported_image_format";

        /// <summary>The question has more options than this backend can score (the logprob fallback's cap).</summary>
        public const string TooManyOptionsForBackend = "too_many_options_for_backend";

        /// <summary>No decision route matches the consumer.</summary>
        public const string NoRoute = "no_route";

        /// <summary>The route names an endpoint the endpoint source does not know.</summary>
        public const string EndpointNotFound = "endpoint_not_found";

        /// <summary>The routed endpoint is switched off.</summary>
        public const string EndpointInactive = "endpoint_inactive";

        /// <summary>The routed endpoint is not an HTTP endpoint with an absolute http(s) base URL.</summary>
        public const string EndpointInvalid = "endpoint_invalid";

        /// <summary>The routed endpoint declares no decision protocol.</summary>
        public const string CapabilityMissing = "capability_missing";

        /// <summary>The endpoint's model list does not advertise what the declared protocol needs.</summary>
        public const string CapabilityNotAdvertised = "capability_not_advertised";

        /// <summary>No decision backend is available in this build or composition.</summary>
        public const string NotConfigured = "not_configured";

        /// <summary>The consumer's calls-per-second budget is spent.</summary>
        public const string RateBudgetExceeded = "rate_budget_exceeded";

        /// <summary>The consumer already has its maximum number of calls in flight.</summary>
        public const string InFlightBudgetExceeded = "in_flight_budget_exceeded";

        /// <summary>The per-call deadline expired before the backend answered.</summary>
        public const string DeadlineExceeded = "deadline_exceeded";

        /// <summary>The transport gave up (its own timeout or a socket failure).</summary>
        public const string TransportFailed = "transport_failed";

        /// <summary>The server returned an HTTP error that no more specific reason describes.</summary>
        public const string HttpError = "http_error";

        /// <summary>The server refused a question type it does not implement.</summary>
        public const string UnsupportedQuestionType = "unsupported_question_type";

        /// <summary>The server's model is not a decision model (llama.cpp answers HTTP 501).</summary>
        public const string NotADecisionModel = "not_a_decision_model";

        /// <summary>The response body is not the documented shape.</summary>
        public const string MalformedResponse = "malformed_response";

        /// <summary>The response has no answer for a question that was asked.</summary>
        public const string MissingAnswer = "missing_answer";

        /// <summary>The chat endpoint returned no logprobs, so the fallback cannot score the labels.</summary>
        public const string LogprobsMissing = "logprobs_missing";

        /// <summary>The chat answer never produced one of the label tokens.</summary>
        public const string NoLabelToken = "no_label_token";

        /// <summary>A backend threw instead of returning a result (a contract violation, caught by consumers).</summary>
        public const string ClientThrew = "client_threw";
    }
}
