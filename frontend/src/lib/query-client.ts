import { QueryClient } from "@tanstack/react-query";
import { mockClient } from "./api";

export const client = mockClient;

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5_000,
      refetchOnWindowFocus: false,
      retry: 1,
    },
  },
});
