import { QueryClient } from "@tanstack/react-query";
import { httpClient } from "./api";

export const client = httpClient;

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5_000,
      refetchOnWindowFocus: false,
      retry: 1,
    },
  },
});
